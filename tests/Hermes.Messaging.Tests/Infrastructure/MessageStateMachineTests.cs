using Hermes.Messaging;

namespace Hermes.Messaging.Tests.Infrastructure;

/// <summary>
/// HERMES-002 — durable state machine and recovery tests at the store level.
/// These are deterministic (no host/timing) and cover the transitions and crash windows.
/// </summary>
[Collection("PersistentMessageStore")]
public class MessageStateMachineTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly PersistentMessageStore<TestMessage> _store;

    public MessageStateMachineTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"sm_{Guid.NewGuid()}.db");
        _store = new PersistentMessageStore<TestMessage>(_testDbPath, TimeSpan.FromSeconds(1));
    }

    private Guid InsertPending(string path = "route")
    {
        var messageId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        _store.Insert(new ChannelMessage<TestMessage>(path, new TestMessage("data"), Guid.CreateVersion7(DateTimeOffset.UtcNow), messageId));
        return messageId;
    }

    [Fact]
    public void TryClaim_Pending_TransitionsToProcessing_AndIncrementsAttempt()
    {
        var id = InsertPending();

        var claimed = _store.TryClaim(id);

        Assert.NotNull(claimed);
        Assert.Equal(MessageStatus.Processing, claimed.Status);
        Assert.Equal(1, claimed.AttemptCount);
        Assert.Equal(MessageStatus.Processing, _store.GetByMessageId(id)!.Status);
    }

    [Fact]
    public void TryClaim_Twice_SecondClaimFails_NoDuplicateProcessing()
    {
        var id = InsertPending();

        var first = _store.TryClaim(id);
        var second = _store.TryClaim(id);

        Assert.NotNull(first);
        Assert.Null(second); // duplicate-signal safety: already Processing
    }

    [Fact]
    public void TryClaim_Completed_Fails()
    {
        var id = InsertPending();
        _store.TryClaim(id);
        _store.MarkCompleted(id);

        Assert.Null(_store.TryClaim(id));
    }

    [Fact]
    public void TryClaim_DeadLettered_Fails()
    {
        var id = InsertPending();
        _store.TryClaim(id);
        _store.MarkDeadLettered(id, "boom");

        Assert.Null(_store.TryClaim(id));
    }

    [Fact]
    public void TryClaim_Missing_Fails()
    {
        Assert.Null(_store.TryClaim(Guid.CreateVersion7(DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void ScheduleRetry_FutureDue_NotClaimableYet_ThenClaimableWhenDue()
    {
        var id = InsertPending();
        _store.TryClaim(id);

        // Not yet due.
        _store.ScheduleRetry(id, DateTimeOffset.UtcNow.AddMinutes(5), "boom");
        Assert.Null(_store.TryClaim(id));

        // Now due.
        _store.ScheduleRetry(id, DateTimeOffset.UtcNow.AddMilliseconds(-1), "boom");
        var claimed = _store.TryClaim(id);
        Assert.NotNull(claimed);
        Assert.Equal(MessageStatus.Processing, claimed.Status);
        Assert.Equal(2, claimed.AttemptCount); // incremented again on re-claim
    }

    [Fact]
    public void GetDueMessages_ReturnsPendingAndDueRetries_ExcludesFutureRetries()
    {
        var pending = InsertPending("p");

        var dueRetry = InsertPending("dr");
        _store.TryClaim(dueRetry);
        _store.ScheduleRetry(dueRetry, DateTimeOffset.UtcNow.AddMinutes(-1), "boom");

        var futureRetry = InsertPending("fr");
        _store.TryClaim(futureRetry);
        _store.ScheduleRetry(futureRetry, DateTimeOffset.UtcNow.AddMinutes(10), "boom");

        var processing = InsertPending("pr");
        _store.TryClaim(processing); // stays Processing, not due

        var due = _store.GetDueMessages(DateTimeOffset.UtcNow).Select(m => m.MessageId).ToHashSet();

        Assert.Contains(pending, due);
        Assert.Contains(dueRetry, due);
        Assert.DoesNotContain(futureRetry, due);
        Assert.DoesNotContain(processing, due);
    }

    [Fact]
    public void RecoverInterrupted_MovesProcessingBackToPending()
    {
        var a = InsertPending("a");
        var b = InsertPending("b");
        _store.TryClaim(a);
        _store.TryClaim(b);

        // Simulate crash: both are left Processing.
        Assert.Equal(MessageStatus.Processing, _store.GetByMessageId(a)!.Status);
        Assert.Equal(MessageStatus.Processing, _store.GetByMessageId(b)!.Status);

        var recovered = _store.RecoverInterrupted();

        Assert.Equal(2, recovered);
        Assert.Equal(MessageStatus.Pending, _store.GetByMessageId(a)!.Status);
        Assert.Equal(MessageStatus.Pending, _store.GetByMessageId(b)!.Status);
        // Recovered messages are claimable again.
        Assert.NotNull(_store.TryClaim(a));
    }

    [Fact]
    public void RecoverInterrupted_LeavesCompletedAndDeadLetteredUntouched()
    {
        var completed = InsertPending("c");
        _store.TryClaim(completed);
        _store.MarkCompleted(completed);

        var dead = InsertPending("d");
        _store.TryClaim(dead);
        _store.MarkDeadLettered(dead, "boom");

        _store.RecoverInterrupted();

        Assert.Equal(MessageStatus.Completed, _store.GetByMessageId(completed)!.Status);
        Assert.Equal(MessageStatus.DeadLettered, _store.GetByMessageId(dead)!.Status);
    }

    [Fact]
    public void ReplayDeadLetter_MovesDeadLetteredToPending()
    {
        var id = InsertPending();
        _store.TryClaim(id);
        _store.MarkDeadLettered(id, "boom");

        var replayed = _store.ReplayDeadLetter(id);

        Assert.True(replayed);
        Assert.Equal(MessageStatus.Pending, _store.GetByMessageId(id)!.Status);
        Assert.NotNull(_store.TryClaim(id));
    }

    [Fact]
    public void ReplayDeadLetter_NonDeadLettered_ReturnsFalse()
    {
        var id = InsertPending();
        Assert.False(_store.ReplayDeadLetter(id)); // still Pending
    }

    [Fact]
    public void CrashWindow_AfterClaimBeforeComplete_RecoveredToPendingOnRestart()
    {
        // Simulate: publisher committed Pending, worker claimed (Processing), then crash
        // before MarkCompleted. Re-open the store and recover.
        var id = InsertPending();
        _store.TryClaim(id);
        _store.Dispose();

        using var reopened = new PersistentMessageStore<TestMessage>(_testDbPath, TimeSpan.FromSeconds(1));
        var recovered = reopened.RecoverInterrupted();

        Assert.Equal(1, recovered);
        Assert.Equal(MessageStatus.Pending, reopened.GetByMessageId(id)!.Status);
    }

    [Fact]
    public void RetryScheduled_SurvivesRestart()
    {
        var id = InsertPending();
        _store.TryClaim(id);
        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        _store.ScheduleRetry(id, due, "boom");
        _store.Dispose();

        using var reopened = new PersistentMessageStore<TestMessage>(_testDbPath, TimeSpan.FromSeconds(1));
        var persisted = reopened.GetByMessageId(id);

        Assert.NotNull(persisted);
        Assert.Equal(MessageStatus.RetryScheduled, persisted.Status);
        Assert.NotNull(persisted.NextAttemptAt);
        // Due retry is still claimable after restart.
        Assert.NotNull(reopened.TryClaim(id));
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { /* best-effort */ }
        }
    }

    private sealed record TestMessage(string Value);
}
