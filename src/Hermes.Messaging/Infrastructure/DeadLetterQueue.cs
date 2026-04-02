using System.Threading.Channels;
using Hermes.Messaging.Domain.Interfaces;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Dead Letter Queue for messages that failed after max retry attempts.
/// </summary>
/// <typeparam name="T">Message payload type.</typeparam>
public sealed class DeadLetterQueue<T> : IDeadLetterQueue
{
    private readonly Channel<DeadLetterMessage<T>> _channel;
    
    public DeadLetterQueue(int capacity = 10000)
    {
        _channel = Channel.CreateBounded<DeadLetterMessage<T>>(new BoundedChannelOptions(capacity)
        {
            // TryEnqueue uses TryWrite which returns false when full (FullMode is irrelevant).
            // EnqueueAsync uses WriteAsync which respects FullMode — Wait applies backpressure
            // instead of silently dropping messages.
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public Type MessageType => typeof(T);
    public ChannelReader<DeadLetterMessage<T>> Reader => _channel.Reader;
    public ChannelWriter<DeadLetterMessage<T>> Writer => _channel.Writer;

    public async Task EnqueueAsync(string path, T body, Exception exception, int attempts, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var deadLetter = new DeadLetterMessage<T>(path, body, exception, attempts, correlationId, DateTimeOffset.UtcNow);
        await _channel.Writer.WriteAsync(deadLetter, cancellationToken);
    }

    public bool TryEnqueue(string path, T body, Exception exception, int attempts, Guid correlationId)
    {
        var deadLetter = new DeadLetterMessage<T>(path, body, exception, attempts, correlationId, DateTimeOffset.UtcNow);
        return _channel.Writer.TryWrite(deadLetter);
    }

    public Task<DeadLetterReadResult> TryReadAsync(CancellationToken cancellationToken = default)
    {
        if (_channel.Reader.TryRead(out var deadLetter))
        {
            return Task.FromResult(new DeadLetterReadResult(true, deadLetter));
        }

        return Task.FromResult(new DeadLetterReadResult(false, null));
    }
}

public sealed record DeadLetterMessage<T>(
    string Path,
    T Body,
    Exception Exception,
    int Attempts,
    Guid CorrelationId,
    DateTimeOffset FailedAt);
