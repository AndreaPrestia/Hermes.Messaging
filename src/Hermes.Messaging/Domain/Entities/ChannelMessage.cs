namespace Hermes.Messaging.Domain.Entities;

/// <summary>
/// Represents a message routed through the in-process channel infrastructure.
/// </summary>
/// <typeparam name="T">Payload type.</typeparam>
public sealed record ChannelMessage<T>(string Path, T Body, Guid CorrelationId);
