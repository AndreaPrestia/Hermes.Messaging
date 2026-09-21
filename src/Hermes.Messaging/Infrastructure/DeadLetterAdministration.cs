
namespace Hermes.Messaging;

/// <summary>
/// A durable dead-letter entry exposed for administration. This is a read model that does not
/// expose the underlying storage engine.
/// </summary>
/// <typeparam name="T">Message payload type.</typeparam>
public sealed record DeadLetterEntry<T>
{
    public required Guid MessageId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required string Route { get; init; }
    public required T Body { get; init; }
    public required int AttemptCount { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset DeadLetteredAt { get; init; }
    public string? LastError { get; init; }
}

/// <summary>
/// Administrative surface for the durable dead-letter store of a given message type.
/// Inspection is non-destructive; replay, delete and purge are explicit operations.
/// </summary>
/// <typeparam name="T">Message payload type.</typeparam>
public interface IDeadLetterAdministration<T>
{
    /// <summary>Lists dead-lettered entries with paging (non-destructive).</summary>
    IReadOnlyList<DeadLetterEntry<T>> List(int skip = 0, int take = 100);

    /// <summary>Gets a single dead-lettered entry by id, or null if not found / not dead-lettered.</summary>
    DeadLetterEntry<T>? Get(Guid messageId);

    /// <summary>Explicitly returns a dead-lettered message to Pending for reprocessing.</summary>
    bool Replay(Guid messageId);

    /// <summary>Deletes a single dead-lettered message.</summary>
    bool Delete(Guid messageId);

    /// <summary>Deletes all dead-lettered messages. Returns the number removed.</summary>
    int Purge();
}

/// <summary>
/// Store-backed implementation of <see cref="IDeadLetterAdministration{T}"/>.
/// </summary>
/// <typeparam name="T">Message payload type.</typeparam>
internal sealed class DeadLetterAdministration<T> : IDeadLetterAdministration<T>
{
    private readonly IMessageStore<T> _store;

    public DeadLetterAdministration(IMessageStore<T> store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public IReadOnlyList<DeadLetterEntry<T>> List(int skip = 0, int take = 100)
        => _store.ListDeadLetters(skip, take).Select(Map).ToList();

    public DeadLetterEntry<T>? Get(Guid messageId)
    {
        var m = _store.GetByMessageId(messageId);
        return m is { Status: MessageStatus.DeadLettered } ? Map(m) : null;
    }

    public bool Replay(Guid messageId) => _store.ReplayDeadLetter(messageId);

    public bool Delete(Guid messageId) => _store.DeleteDeadLetter(messageId);

    public int Purge() => _store.PurgeDeadLetters();

    private static DeadLetterEntry<T> Map(PersistedMessage<T> m) => new()
    {
        MessageId = m.MessageId,
        CorrelationId = m.CorrelationId,
        Route = m.Path,
        Body = m.Body,
        AttemptCount = m.AttemptCount,
        CreatedAt = m.CreatedAt,
        DeadLetteredAt = m.UpdatedAt,
        LastError = m.LastError
    };
}
