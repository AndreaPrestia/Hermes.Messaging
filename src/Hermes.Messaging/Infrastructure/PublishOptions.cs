namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Optional settings supplied when publishing a message.
/// </summary>
public sealed class PublishOptions
{
    /// <summary>
    /// Logical correlation identifier. If not provided, Hermes generates one.
    /// This value is NOT unique — multiple messages may share the same correlation id.
    /// It is never used as the unique durable persistence key.
    /// </summary>
    public Guid? CorrelationId { get; set; }
}
