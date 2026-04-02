using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Hermes.Messaging.Domain.Interfaces;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Interface for handling dead letter messages of a specific type.
/// </summary>
/// <typeparam name="T">Message payload type.</typeparam>
public interface IDeadLetterHandler<T>
{
    Task HandleAsync(DeadLetterMessage<T> deadLetter, CancellationToken cancellationToken);
}

/// <summary>
/// Single background service that processes all dead letter queues registered in the system.
/// Polls all queues and dispatches to registered handlers based on message type.
/// </summary>
public sealed class DeadLetterQueueProcessor : BackgroundService
{
    private readonly DeadLetterQueueRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeadLetterQueueProcessor> _logger;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int MaxBatchSize = 100;

    // Cached reflection: one MethodInfo per body type
    private static readonly MethodInfo ProcessMethod =
        typeof(DeadLetterQueueProcessor).GetMethod(nameof(ProcessDeadLetterGenericAsync),
            BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException(nameof(DeadLetterQueueProcessor), nameof(ProcessDeadLetterGenericAsync));

    private static readonly ConcurrentDictionary<Type, MethodInfo> GenericMethodCache = new();

    public DeadLetterQueueProcessor(
        DeadLetterQueueRegistry registry,
        IServiceScopeFactory scopeFactory,
        ILogger<DeadLetterQueueProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Dead Letter Queue Processor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Re-fetch every cycle to pick up queues registered after startup
                var queues = _registry.GetAll();

                foreach (var queue in queues)
                {
                    await DrainQueueAsync(queue, stoppingToken);
                }

                // Wait before next poll
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in dead letter queue processor");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task DrainQueueAsync(IDeadLetterQueue queue, CancellationToken cancellationToken)
    {
        // Process up to MaxBatchSize messages per queue per cycle to ensure fair scheduling
        for (var i = 0; i < MaxBatchSize; i++)
        {
            var result = await queue.TryReadAsync(cancellationToken);
            if (!result.Success || result.Message is null)
            {
                break;
            }

            var messageType = result.Message.GetType();
            var bodyType = messageType.GetGenericArguments()[0];

            var genericMethod = GenericMethodCache.GetOrAdd(bodyType,
                t => ProcessMethod.MakeGenericMethod(t));

            await (Task)genericMethod.Invoke(this, [result.Message, cancellationToken])!;
        }
    }

    private async Task ProcessDeadLetterGenericAsync<T>(DeadLetterMessage<T> deadLetter, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        
        _logger.LogWarning(
            "Processing dead letter for path '{Path}', CorrelationId: {CorrelationId} - Failed {Attempts} times at {FailedAt}. Error: {Error}",
            deadLetter.Path,
            deadLetter.CorrelationId,
            deadLetter.Attempts,
            deadLetter.FailedAt,
            deadLetter.Exception.Message);

        try
        {
            // Try to get a registered handler for this message type
            var handler = scope.ServiceProvider.GetService<IDeadLetterHandler<T>>();
            
            if (handler is not null)
            {
                await handler.HandleAsync(deadLetter, cancellationToken);
            }
            else
            {
                // No handler registered - just log
                _logger.LogWarning(
                    "No dead letter handler registered for type {MessageType}. Message will be logged and dropped.",
                    typeof(T).Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing dead letter for path '{Path}', CorrelationId: {CorrelationId}. Message: {MessageType}", 
                deadLetter.Path, deadLetter.CorrelationId, typeof(T).Name);
        }
    }
}
