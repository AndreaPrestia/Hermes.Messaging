using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Hermes.Messaging.Domain.Entities;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Enhanced subscriber with persistent message storage for crash recovery.
/// Messages are persisted before processing and replayed on startup.
/// </summary>
/// <typeparam name="T">Message payload type.</typeparam>
public sealed class PersistentChannelRouterSubscriber<T> : BackgroundService
{
    private readonly ChannelReader<ChannelMessage<T>> _reader;
    private readonly ChannelRouteTable<T> _routes;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PersistentChannelRouterSubscriber<T>>? _logger;
    private readonly DeadLetterQueue<T> _deadLetterQueue;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly PersistentMessageStore<T> _messageStore;
    private readonly TimeProvider _timeProvider;

    private readonly int _maxRetryAttempts;
    private readonly TimeSpan _initialRetryDelay;
    private readonly TimeSpan _maxRetryDelay;
    private readonly int _maxConcurrency;
    private readonly TimeSpan _shutdownGracePeriod;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(1);
    private static readonly string CircuitKeyPrefix = typeof(T).Name + ":";

    // Multiple fixed worker loops read from this channel concurrently, so SingleReader=false.
    // Duplicate delivery of the same MessageId is harmless because TryClaim is atomic.
    private readonly Channel<Guid> _wakeups = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

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
        CircuitBreaker circuitBreaker,
        PersistentMessageStore<T> messageStore,
        IEnumerable<ChannelRouteRegistration<T>> registrations,
        MessageBusOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(channelRegistry);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(deadLetterQueue);
        ArgumentNullException.ThrowIfNull(circuitBreaker);
        ArgumentNullException.ThrowIfNull(messageStore);

        _reader = channelRegistry.GetOrCreate<T>().Reader;
        _routes = routes;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _deadLetterQueue = deadLetterQueue;
        _circuitBreaker = circuitBreaker;
        _messageStore = messageStore;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _maxRetryAttempts = options?.MaxRetryAttempts ?? 3;
        _initialRetryDelay = TimeSpan.FromMilliseconds(options?.InitialRetryDelayMs ?? 100);
        _maxRetryDelay = TimeSpan.FromSeconds(30);
        _maxConcurrency = Math.Max(1, options?.MaxConcurrency ?? 1);
        _shutdownGracePeriod = options?.ShutdownGracePeriod ?? TimeSpan.FromSeconds(30);

        _ = registrations.ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup interrupted-recovery: any message left Processing (or the retired Failed
        // state) by a previous crash is returned to Pending so it can be re-claimed.
        var recovered = _messageStore.RecoverInterrupted();
        if (recovered > 0)
        {
            _logger?.LogInformation("Recovered {Count} interrupted messages to Pending on startup", recovered);
        }

        // Seed the wake-up channel with all currently due work (Pending + due retries).
        // This also covers signals that were lost before this process started.
        SignalDueWork();

        // Start background loops with proper lifecycle.
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
                _wakeups.Writer.TryWrite(envelope.MessageId);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }

    private void SignalDueWork()
    {
        foreach (var due in _messageStore.GetDueMessages(_timeProvider.GetUtcNow()))
        {
            _wakeups.Writer.TryWrite(due.MessageId);
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

        try
        {
            await DispatchOnceAsync(envelope, scope.ServiceProvider, stoppingToken).ConfigureAwait(false);
            _messageStore.MarkCompleted(claimed.MessageId);
        }
        catch (CircuitBreakerOpenException)
        {
            // An open circuit must NOT count as a failed attempt or lead to dead-lettering
            // (SDD 07). Reschedule the message and undo the attempt increment from the claim.
            var nextAttemptAt = _timeProvider.GetUtcNow() + ComputeBackoff(Math.Max(1, claimed.AttemptCount));
            _messageStore.ScheduleRetry(claimed.MessageId, nextAttemptAt, "Circuit breaker open");
            if (claimed.AttemptCount > 0)
            {
                _messageStore.DecrementAttempt(claimed.MessageId);
            }
            _logger?.LogWarning("Circuit open for path '{Path}', MessageId: {MessageId} — rescheduled without consuming an attempt", claimed.Path, claimed.MessageId);
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
        if (claimed.AttemptCount >= _maxRetryAttempts)
        {
            _logger?.LogError(ex, "Message exhausted retries for path '{Path}', MessageId: {MessageId} — dead lettering", claimed.Path, claimed.MessageId);
            DeadLetter(claimed, ex);
            return;
        }

        // Durably schedule a future retry with jittered exponential backoff. The worker is
        // NOT held by the delay — the reconciliation loop re-signals the message when due.
        var nextAttemptAt = _timeProvider.GetUtcNow() + ComputeBackoff(claimed.AttemptCount);
        ChannelMetrics.RecordRetry(typeof(T), claimed.Path);
        _logger?.LogWarning(ex, "Scheduling retry for path '{Path}', MessageId: {MessageId} (attempt {Attempt}/{MaxAttempts}) at {NextAttemptAt:o}",
            claimed.Path, claimed.MessageId, claimed.AttemptCount, _maxRetryAttempts, nextAttemptAt);
        _messageStore.ScheduleRetry(claimed.MessageId, nextAttemptAt, ex.Message);
    }

    private void DeadLetter(PersistedMessage<T> claimed, Exception ex)
    {
        // Durable record FIRST — this is the source of truth and must never depend on the
        // volatile observer channel. The observer enqueue below is best-effort only.
        _messageStore.MarkDeadLettered(claimed.MessageId, ex.Message);

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
        var circuitKey = CircuitKeyPrefix + envelope.Path;
        if (_circuitBreaker.IsOpen(circuitKey))
        {
            _logger?.LogWarning("Circuit breaker is open for route '{Path}', MessageId: {MessageId} - skipping dispatch", envelope.Path, envelope.MessageId);
            throw new CircuitBreakerOpenException($"Circuit breaker open for route: {envelope.Path}");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _routes.DispatchAsync(envelope.Path, envelope.Body, services, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            ChannelMetrics.RecordDispatchSuccess(typeof(T), envelope.Path, stopwatch.Elapsed.TotalMilliseconds);
            _circuitBreaker.RecordSuccess(circuitKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            ChannelMetrics.RecordDispatchFailure(typeof(T), envelope.Path, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
        catch (Exception)
        {
            stopwatch.Stop();
            ChannelMetrics.RecordDispatchFailure(typeof(T), envelope.Path, stopwatch.Elapsed.TotalMilliseconds);
            _circuitBreaker.RecordFailure(circuitKey);
            throw;
        }
    }
}
