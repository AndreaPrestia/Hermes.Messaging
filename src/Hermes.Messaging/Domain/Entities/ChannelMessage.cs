namespace Hermes.Messaging.Domain.Entities;

/// <summary>
/// Represents a message routed through the in-process channel infrastructure.
/// </summary>
/// <remarks>
/// <see cref="MessageId"/> is the unique technical identity of a single message.
/// <see cref="CorrelationId"/> is a logical correlation value that may be caller-supplied
/// or generated, and is explicitly NOT unique — several messages may share it.
/// Never use <see cref="CorrelationId"/> as a unique persistence key.
/// </remarks>
/// <typeparam name="T">Payload type.</typeparam>
public sealed record ChannelMessage<T>(string Path, T Body, Guid CorrelationId, Guid MessageId)
{
    /// <summary>
    /// Backwards-friendly constructor that generates a fresh unique <see cref="MessageId"/>.
    /// </summary>
    public ChannelMessage(string Path, T Body, Guid CorrelationId)
        : this(Path, Body, CorrelationId, Guid.CreateVersion7(DateTimeOffset.UtcNow))
    {
    }
}
