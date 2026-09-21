using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


namespace Hermes.Messaging;

/// <summary>
/// Enhanced subscriber with persistent message storage for crash recovery.
/// Messages are persisted before processing and replayed on startup.
/// </summary>
/// <typeparam name="T">Message payload type.</typeparam>
internal sealed class PersistentChannelRouterSubscriber<T> : BackgroundService
{
    private readonly ChannelReader<ChannelMessage<T>> _reader;
    private readonly ChannelRouteTable<T> _routes;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PersistentChannelRouterSubscriber<T>>? _logger;
    private readonly DeadLetterQueue<T> _deadLetterQueue;
    private readonly IMessageStore<T> _messageStore;
    private readonly TimeProvider _timeProvider;
    private readonly HermesReadiness? _readiness;
    private readonly HermesStoreMetrics? _storeMetrics;

    private readonly int _maxAttempts;
    private readonly TimeSpan _initialRetryDelay;
    private readonly TimeSpan _maxRetryDelay;
    private readonly int _maxConcurrency;
    private readonly TimeSpan _shutdownGracePeriod;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(1);

    // Bounded wake-up notification with a dedup set (P2): the durable store remains the source of
    // truth; this layer only accelerates. Capacity is bounded so a slow consumer cannot cause
    // unbounded memory growth, and duplicate due-scans do not enqueue the same id repeatedly.
    private const int WakeupCapacity = 4096;
    private readonly Channel<Guid> _wakeups = Channel.CreateBounded<Guid>(new BoundedChannelOptions(WakeupCapacity)
    {
        SingleReader = false,
        SingleWriter = false,
        // TryWrite returns false when full; we react by clearing the outstanding marker so the
        // reconciliation loop can re-signal later. The durable store is authoritative.
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _outstanding = new();

    private CancellationTokenSource? _cleanupCts;
    private Task? _cleanupTask;
    private CancellationTokenSource? _reconcileCts;
    private Task? _reconcileTask;

    public PersistentChannelRouterSubscriber(
        ChannelRouteTable<T> routes,
        IServiceScopeFactory scopeFactory,
        ChannelRegistry channelRegistry,
        ILogger<PersistentChannelRouterSubscriber<T>>? logger,
        DeadLetterQueue<T> deadLetterQueue,
        IMessageStore<T> messageStore,
        IEnumerable<ChannelRouteRegistration<T>> registrations,
        MessageBusOptions? options = null,
        TimeProvider? timeProvider = null,
        HermesReadiness? readiness = null,
        HermesStoreMetrics? storeMetrics = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(channelRegistry);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(deadLetterQueue);
        ArgumentNullException.ThrowIfNull(messageStore);

        _reader = channelRegistry.GetOrCreate<T>().Reader;
        _routes = routes;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _deadLetterQueue = deadLetterQueue;
        _messageStore = messageStore;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _maxAttempts = options?.MaxAttempts ?? 3;
        _initialRetryDelay = TimeSpan.FromMilliseconds(options?.InitialRetryDelayMs ?? 100);
        _maxRetryDelay = TimeSpan.FromSeconds(30);
        _maxConcurrency = Math.Max(1, options?.MaxConcurrency ?? 1);
        _shutdownGracePeriod = options?.ShutdownGracePeriod ?? TimeSpan.FromSeconds(30);
        _readiness = readiness;
        _storeMetrics = storeMetrics;

        _readiness?.Expect(TypeIdentity.Key<T>());
        _storeMetrics?.Register(TypeIdentity.Key<T>(), _messageStore.GetStats);

        _ = registrations.ToArray();
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Perform startup recovery SYNCHRONOUSLY during host startup (awaited by the host),
        // BEFORE the loop is running and before the runtime is marked Ready. This guarantees a
        // publish cannot be accepted until recovery is complete (HERMES-006 P1).
        var recovered = _messageStore.RecoverInterrupted();
        if (recovered > 0)
        {
            _logger?.LogInformation("Recovered {Count} interrupted messages to Pending on startup", recovered);
        }

        // Seed the wake-up channel with all currently due work (Pending + due retries),
        // covering signals lost before this process started.
        SignalDueWork();

        // This type's recovery is complete. Publishability requires ALL expected subscribers to
        // have recovered; the publish gate and IsReady combine runtime state with this signal.
        _readiness?.MarkRecovered(TypeIdentity.Key<T>());

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Recovery + due-work seeding already ran in StartAsync. Start background loops.
        _cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _cleanupTask = CleanupLoopAsync(_cleanupCts.Token);

        _reconcileCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _reconcileTask = ReconcileLoopAsync(_reconcileCts.Token);

        // Bridge the incoming fast-path channel (ChannelMessage<T> from the publisher) into
        // the internal wake-up channel keyed by MessageId. The durable store is the source
        // of truth; the wake-up is only an acceleration signal.
        var bridge = BridgeIncomingSignalsAsync(stoppingToken);

        // Fixed pool of async worker loops. No per-message Task.Run and no SemaphoreSlim:
        // each worker awaits the shared wake-up channel and processes one message at a time.
        var workers = new Task[_maxConcurrency];
        for (var i = 0; i < _maxConcurrency; i++)
        {
            workers[i] = WorkerLoopAsync(stoppingToken);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        await bridge.ConfigureAwait(false);
    }

    private async Task WorkerLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var messageId in _wakeups.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                // Clear the outstanding marker BEFORE claiming so that a concurrent due-scan can
                // re-signal this id if it still needs work after this attempt. This guarantees a
                // message can never be permanently suppressed by dedup bookkeeping.
                _outstanding.TryRemove(messageId, out _);
                await ProcessMessageAsync(messageId, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }

    private async Task BridgeIncomingSignalsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var envelope in _reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                TrySignal(envelope.MessageId);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }

    private void SignalDueWork()
    {
        // Only query as many due IDs as the wake-up channel can actually accept right now. This
        // keeps reconciliation bounded regardless of backlog size: we never materialize the whole
        // durable backlog just to signal a bounded number of IDs. Startup seeding uses this same
        // path, so it is bounded too. The durable store remains the source of truth; anything not
        // signalled this cycle is picked up by a later reconciliation as workers make progress.
        var available = WakeupCapacity - _outstanding.Count;
        if (available <= 0)
        {
            return;
        }

        var dueIds = _messageStore.GetDueMessageIds(_timeProvider.GetUtcNow(), available);
        foreach (var id in dueIds)
        {
            TrySignal(id);
        }
    }

    /// <summary>
    /// Enqueues a wake-up for <paramref name="messageId"/> unless one is already outstanding.
    /// Bounded and de-duplicated: repeated due-scans for a slow consumer cannot accumulate
    /// duplicate ids, and a full channel simply drops the (recoverable) signal.
    /// </summary>
    private void TrySignal(Guid messageId)
    {
        // Reserve the slot first; if an identical id is already outstanding, do nothing.
        if (!_outstanding.TryAdd(messageId, 0))
        {
            return;
        }

        if (!_wakeups.Writer.TryWrite(messageId))
        {
            // Channel is at capacity — release the marker so reconciliation can retry later.
            // The durable store remains the source of truth; nothing is lost.
            _outstanding.TryRemove(messageId, out _);
        }
    }

    /// <summary>
    /// Claims a message and performs a single processing attempt. Duplicate signals are
    /// safe: only the claim that transitions the record to Processing proceeds; any other
    /// signal for the same message finds it un-claimable and is ignored.
    /// </summary>
    private async Task ProcessMessageAsync(Guid messageId, CancellationToken stoppingToken)
    {
        // Atomic claim: Pending|due-RetryScheduled -> Processing (+ attempt increment).
        var claimed = _messageStore.TryClaim(messageId);
        if (claimed is null)
        {
            // Not claimable: already Processing/Completed/DeadLettered, missing, or a
            // duplicate signal. Nothing to do.
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        ChannelMetrics.RecordDequeued(typeof(T), claimed.Path);

        var envelope = new ChannelMessage<T>(claimed.Path, claimed.Body, claimed.CorrelationId, claimed.MessageId);

        using var activity = HermesTelemetry.ActivitySource.StartActivity("Process", ActivityKind.Consumer);
        activity?.SetTag("message_type", typeof(T).Name);
        activity?.SetTag("route", claimed.Path);

        HermesTelemetry.ProcessingStarted.Add(1, HermesTelemetry.Tags(typeof(T), claimed.Path));
        var startTs = Stopwatch.GetTimestamp();

        try
        {
            await DispatchOnceAsync(envelope, scope.ServiceProvider, stoppingToken).ConfigureAwait(false);
            _messageStore.MarkCompleted(claimed.MessageId);
            HermesTelemetry.ProcessingSucceeded.Add(1, HermesTelemetry.Tags(typeof(T), claimed.Path, "completed"));
            HermesTelemetry.ProcessingDuration.Record(Stopwatch.GetElapsedTime(startTs).TotalMilliseconds, HermesTelemetry.Tags(typeof(T), claimed.Path, "completed"));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown mid-processing: leave as Processing so startup recovery returns it to
            // Pending on the next run. Do not surface as a failure.
            _logger?.LogDebug("Processing cancelled during shutdown for MessageId: {MessageId} — will recover on next startup", claimed.MessageId);
        }
        catch (Exception ex)
        {
            HandleAttemptFailure(claimed, ex, stoppingToken.IsCancellationRequested);
        }
    }

    private void HandleAttemptFailure(PersistedMessage<T> claimed, Exception ex, bool shutdownRequested)
    {
        var disposition = RetryClassifier.Classify(ex, shutdownRequested);

        if (disposition == FailureDisposition.Shutdown)
        {
            // Handler propagated cancellation during shutdown: leave for recovery on restart.
            _logger?.LogDebug("Handler cancelled during shutdown for MessageId: {MessageId} — will recover on next startup", claimed.MessageId);
            return;
        }

        if (disposition == FailureDisposition.DeadLetter)
        {
            _logger?.LogError(ex, "Non-retryable failure for path '{Path}', MessageId: {MessageId} — dead lettering (no retry)", claimed.Path, claimed.MessageId);
            DeadLetter(claimed, ex);
            return;
        }

        // Retryable. claimed.AttemptCount already reflects the attempt just made (incremented at claim).
        if (claimed.AttemptCount >= _maxAttempts)
        {
            _logger?.LogError(ex, "Message exhausted attempts for path '{Path}', MessageId: {MessageId} — dead lettering", claimed.Path, claimed.MessageId);
            DeadLetter(claimed, ex);
            return;
        }

        // Durably schedule a future retry with jittered exponential backoff. The worker is
        // NOT held by the delay — the reconciliation loop re-signals the message when due.
        var nextAttemptAt = _timeProvider.GetUtcNow() + ComputeBackoff(claimed.AttemptCount);
        ChannelMetrics.RecordRetry(typeof(T), claimed.Path);
        HermesTelemetry.ProcessingFailed.Add(1, HermesTelemetry.Tags(typeof(T), claimed.Path, "retry_scheduled"));
        HermesTelemetry.ProcessingRetried.Add(1, HermesTelemetry.Tags(typeof(T), claimed.Path));
        _logger?.LogWarning(ex, "Scheduling retry for path '{Path}', MessageId: {MessageId} (attempt {Attempt}/{MaxAttempts}) at {NextAttemptAt:o}",
            claimed.Path, claimed.MessageId, claimed.AttemptCount, _maxAttempts, nextAttemptAt);
        _messageStore.ScheduleRetry(claimed.MessageId, nextAttemptAt, ex.Message);
    }

    private void DeadLetter(PersistedMessage<T> claimed, Exception ex)
    {
        // Durable record FIRST — this is the source of truth and must never depend on the
        // volatile observer channel. The observer enqueue below is best-effort only.
        _messageStore.MarkDeadLettered(claimed.MessageId, ex.Message);
        HermesTelemetry.ProcessingFailed.Add(1, HermesTelemetry.Tags(typeof(T), claimed.Path, "deadlettered"));
        HermesTelemetry.ProcessingDeadLettered.Add(1, HermesTelemetry.Tags(typeof(T), claimed.Path));

        if (!_deadLetterQueue.TryEnqueue(claimed.Path, claimed.Body, ex, claimed.AttemptCount, claimed.CorrelationId))
        {
            // The durable DeadLettered record still exists; only the observer notification was dropped.
            _logger?.LogWarning("Dead letter observer queue is full for path '{Path}', MessageId: {MessageId} — durable record retained", claimed.Path, claimed.MessageId);
            ChannelMetrics.RecordDropped(typeof(T), claimed.Path);
        }
    }

    private TimeSpan ComputeBackoff(int attemptCount)
    {
        // attemptCount is 1-based for the attempt just completed.
        var exponent = Math.Max(0, attemptCount - 1);
        var baseMs = _initialRetryDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var cappedMs = Math.Min(baseMs, _maxRetryDelay.TotalMilliseconds);
        // Full jitter in [0.5x, 1.0x] of the capped delay to avoid thundering herds.
        var jittered = cappedMs * (0.5 + Random.Shared.NextDouble() * 0.5);
        return TimeSpan.FromMilliseconds(jittered);
    }

    private async Task ReconcileLoopAsync(CancellationToken cancellationToken)
    {
        // Periodic reconciliation: re-signal due work. This recovers lost fast-path signals
        // and picks up RetryScheduled messages whose NextAttemptAt has arrived.
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ReconcileInterval, cancellationToken).ConfigureAwait(false);
                SignalDueWork();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during reconciliation scan");
            }
        }
    }

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupInterval, cancellationToken);
                
                var deleted = _messageStore.CleanupOldMessages();
                if (deleted > 0)
                {
                    _logger?.LogInformation("Cleaned up {Count} old messages from persistent store", deleted);
                }

                var stats = _messageStore.GetStats();
                _logger?.LogDebug("Message store stats: {Stats}", stats);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during message store cleanup");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop background loops before stopping the processing loop.
        if (_reconcileCts is not null)
        {
            await _reconcileCts.CancelAsync();
            _reconcileCts.Dispose();
        }

        if (_reconcileTask is not null)
        {
            try { await _reconcileTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }

        if (_cleanupCts is not null)
        {
            await _cleanupCts.CancelAsync();
            _cleanupCts.Dispose();
        }

        if (_cleanupTask is not null)
        {
            try { await _cleanupTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }

        // Stop ExecuteAsync (signals stoppingToken, awaits in-flight handlers). Bound the wait
        // by the configured grace period as well as the host's shutdown token — whichever is
        // shorter. We deliberately do NOT drain the entire backlog: any message left
        // Pending/Processing stays durable and is recovered/re-signaled on the next start.
        using var graceCts = new CancellationTokenSource(_shutdownGracePeriod);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, graceCts.Token);
        try
        {
            await base.StopAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Shutdown grace period ({Grace}) elapsed for {Type}; in-flight work left durable for restart", _shutdownGracePeriod, typeof(T).Name);
        }
    }

    /// <summary>
    /// Performs a single dispatch attempt. Retries are handled durably (RetryScheduled),
    /// not inside this method, so a worker is never held by a retry delay.
    /// </summary>
    private async Task DispatchOnceAsync(ChannelMessage<T> envelope, IServiceProvider services, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _routes.DispatchAsync(envelope.Path, envelope.Body, services, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            ChannelMetrics.RecordDispatchSuccess(typeof(T), envelope.Path, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception)
        {
            stopwatch.Stop();
            ChannelMetrics.RecordDispatchFailure(typeof(T), envelope.Path, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }
}
