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

    private readonly int _maxRetryAttempts;
    private readonly TimeSpan _initialRetryDelay;
    private readonly TimeSpan _maxRetryDelay;
    private readonly int _maxConcurrency;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private static readonly string CircuitKeyPrefix = typeof(T).Name + ":";

    private CancellationTokenSource? _cleanupCts;
    private Task? _cleanupTask;

    public PersistentChannelRouterSubscriber(
        ChannelRouteTable<T> routes,
        IServiceScopeFactory scopeFactory,
        ChannelRegistry channelRegistry,
        ILogger<PersistentChannelRouterSubscriber<T>>? logger,
        DeadLetterQueue<T> deadLetterQueue,
        CircuitBreaker circuitBreaker,
        PersistentMessageStore<T> messageStore,
        IEnumerable<ChannelRouteRegistration<T>> registrations,
        MessageBusOptions? options = null)
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

        _maxRetryAttempts = options?.MaxRetryAttempts ?? 3;
        _initialRetryDelay = TimeSpan.FromMilliseconds(options?.InitialRetryDelayMs ?? 100);
        _maxRetryDelay = TimeSpan.FromSeconds(30);
        _maxConcurrency = Math.Max(1, options?.MaxConcurrency ?? 1);

        _ = registrations.ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Replay pending messages from previous crash
        await ReplayPendingMessagesAsync(stoppingToken);

        // Start cleanup task with proper lifecycle
        _cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _cleanupTask = CleanupLoopAsync(_cleanupCts.Token);

        if (_maxConcurrency == 1)
        {
            await ProcessSequentialAsync(stoppingToken);
        }
        else
        {
            await ProcessConcurrentAsync(stoppingToken);
        }
    }

    private async Task ProcessSequentialAsync(CancellationToken stoppingToken)
    {
        await foreach (var envelope in _reader.ReadAllAsync(stoppingToken))
        {
            await ProcessEnvelopeAsync(envelope, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessConcurrentAsync(CancellationToken stoppingToken)
    {
        // INVARIANT: The channel is created with SingleReader=true.
        // This loop is the sole reader — it dequeues sequentially and hands off
        // to worker tasks via Task.Run. Do NOT add a second reader.
        using var semaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        var activeTasks = new List<Task>();

        await foreach (var envelope in _reader.ReadAllAsync(stoppingToken))
        {
            await semaphore.WaitAsync(stoppingToken).ConfigureAwait(false);

            // Remove completed tasks to avoid unbounded list growth
            activeTasks.RemoveAll(t => t.IsCompleted);

            activeTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ProcessEnvelopeAsync(envelope, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    semaphore.Release();
                }
            }, stoppingToken));
        }

        // Wait for all in-flight tasks to complete on shutdown
        await Task.WhenAll(activeTasks).ConfigureAwait(false);
    }

    private async Task ProcessEnvelopeAsync(ChannelMessage<T> envelope, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();

        ChannelMetrics.RecordDequeued(typeof(T), envelope.Path);

        // Persist message BEFORE processing
        _messageStore.Persist(envelope);

        try
        {
            await DispatchWithRetryAsync(envelope, scope.ServiceProvider, stoppingToken).ConfigureAwait(false);

            // Mark as completed
            _messageStore.UpdateStatus(envelope.CorrelationId, MessageStatus.Completed);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Message was persisted as Pending before processing started.
            // It will be replayed on next startup via ReplayPendingMessagesAsync.
            _logger?.LogDebug("Processing cancelled during shutdown for CorrelationId: {CorrelationId} — will replay on next startup", envelope.CorrelationId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Message failed after all retries for path '{Path}', CorrelationId: {CorrelationId} - moving to dead letter queue", envelope.Path, envelope.CorrelationId);

            // Move to dead letter queue
            if (!_deadLetterQueue.TryEnqueue(envelope.Path, envelope.Body, ex, _maxRetryAttempts, envelope.CorrelationId))
            {
                _logger?.LogCritical("Dead letter queue is full — message PERMANENTLY LOST for path '{Path}', CorrelationId: {CorrelationId}", envelope.Path, envelope.CorrelationId);
                ChannelMetrics.RecordDropped(typeof(T), envelope.Path);
            }

            // Mark as dead lettered
            _messageStore.UpdateStatus(envelope.CorrelationId, MessageStatus.DeadLettered, ex.Message);
        }
    }

    private async Task ReplayPendingMessagesAsync(CancellationToken cancellationToken)
    {
        var pending = _messageStore.GetPendingMessages().ToList();
        
        if (pending.Count == 0)
        {
            return;
        }

        _logger?.LogInformation("Replaying {Count} pending messages after crash recovery", pending.Count);

        foreach (var persisted in pending)
        {
            try
            {
                var envelope = new ChannelMessage<T>(persisted.Path, persisted.Body, persisted.CorrelationId);
                
                using var scope = _scopeFactory.CreateScope();
                
                _messageStore.IncrementAttempt(persisted.CorrelationId);
                
                await DispatchWithRetryAsync(envelope, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
                
                _messageStore.UpdateStatus(persisted.CorrelationId, MessageStatus.Completed);
                
                _logger?.LogInformation("Successfully replayed message with CorrelationId: {CorrelationId}", persisted.CorrelationId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to replay message with CorrelationId: {CorrelationId}", persisted.CorrelationId);
                
                if (persisted.AttemptCount >= _maxRetryAttempts)
                {
                    if (!_deadLetterQueue.TryEnqueue(persisted.Path, persisted.Body, ex, persisted.AttemptCount, persisted.CorrelationId))
                    {
                        _logger?.LogCritical("Dead letter queue is full — replayed message PERMANENTLY LOST for path '{Path}', CorrelationId: {CorrelationId}", persisted.Path, persisted.CorrelationId);
                        ChannelMetrics.RecordDropped(typeof(T), persisted.Path);
                    }
                    _messageStore.UpdateStatus(persisted.CorrelationId, MessageStatus.DeadLettered, ex.Message);
                }
                else
                {
                    _messageStore.UpdateStatus(persisted.CorrelationId, MessageStatus.Failed, ex.Message);
                }
            }
        }
        
        _logger?.LogInformation("Replay completed for {Count} messages", pending.Count);
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
        // Cancel and await cleanup task before stopping
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

        // Stop ExecuteAsync first (signals stoppingToken, waits for the read loop to finish).
        // Then drain any messages still in the channel that ExecuteAsync didn't consume.
        // The cancellationToken here is the host shutdown token with a graceful timeout,
        // so dispatch/retry inside drain can still complete within that window.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await DrainPendingMessagesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DispatchWithRetryAsync(ChannelMessage<T> envelope, IServiceProvider services, CancellationToken cancellationToken)
    {
        var attempt = 0;
        var delay = _initialRetryDelay;

        while (true)
        {
            // Check circuit breaker before attempting
            var circuitKey = CircuitKeyPrefix + envelope.Path;
            if (_circuitBreaker.IsOpen(circuitKey))
            {
                _logger?.LogWarning("Circuit breaker is open for route '{Path}', CorrelationId: {CorrelationId} - skipping dispatch", envelope.Path, envelope.CorrelationId);
                throw new CircuitBreakerOpenException($"Circuit breaker open for route: {envelope.Path}");
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                await _routes.DispatchAsync(envelope.Path, envelope.Body, services, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                ChannelMetrics.RecordDispatchSuccess(typeof(T), envelope.Path, stopwatch.Elapsed.TotalMilliseconds);
                _circuitBreaker.RecordSuccess(circuitKey);
                return;
            }
            catch (RouteNotFoundException)
            {
                stopwatch.Stop();
                // Configuration error — no retries, no circuit breaker impact
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                ChannelMetrics.RecordDispatchFailure(typeof(T), envelope.Path, stopwatch.Elapsed.TotalMilliseconds);
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                ChannelMetrics.RecordDispatchFailure(typeof(T), envelope.Path, stopwatch.Elapsed.TotalMilliseconds);
                _circuitBreaker.RecordFailure(circuitKey);

                attempt++;

                if (attempt >= _maxRetryAttempts)
                {
                    throw;
                }

                ChannelMetrics.RecordRetry(typeof(T), envelope.Path);
                _logger?.LogWarning(ex, "Retrying message for path '{Path}', CorrelationId: {CorrelationId} (attempt {Attempt}/{MaxAttempts})", 
                    envelope.Path, envelope.CorrelationId, attempt, _maxRetryAttempts);

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                // Exponential backoff with cap
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _maxRetryDelay.TotalMilliseconds));
            }
        }
    }

    private async Task DrainPendingMessagesAsync(CancellationToken cancellationToken)
    {
        while (_reader.TryRead(out var envelope))
        {
            using var scope = _scopeFactory.CreateScope();
            ChannelMetrics.RecordDequeued(typeof(T), envelope.Path);

            _messageStore.Persist(envelope);

            try
            {
                await DispatchWithRetryAsync(envelope, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
                _messageStore.UpdateStatus(envelope.CorrelationId, MessageStatus.Completed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error draining message for path '{Path}', CorrelationId: {CorrelationId} - moving to dead letter queue", envelope.Path, envelope.CorrelationId);
                if (!_deadLetterQueue.TryEnqueue(envelope.Path, envelope.Body, ex, _maxRetryAttempts, envelope.CorrelationId))
                {
                    _logger?.LogCritical("Dead letter queue is full — drained message PERMANENTLY LOST for path '{Path}', CorrelationId: {CorrelationId}", envelope.Path, envelope.CorrelationId);
                    ChannelMetrics.RecordDropped(typeof(T), envelope.Path);
                }
                _messageStore.UpdateStatus(envelope.CorrelationId, MessageStatus.DeadLettered, ex.Message);
            }
        }
    }
}
