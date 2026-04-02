using Hermes.Messaging.Infrastructure;

namespace Hermes.Messaging.Tests.Infrastructure;

public class DeadLetterQueueTests
{
    [Fact]
    public void MessageType_ReturnsCorrectType()
    {
        var dlq = new DeadLetterQueue<TestMessage>();

        Assert.Equal(typeof(TestMessage), dlq.MessageType);
    }

    [Fact]
    public void TryEnqueue_AddsMessageToQueue()
    {
        var dlq = new DeadLetterQueue<TestMessage>();
        var exception = new InvalidOperationException("Test error");
        var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        var result = dlq.TryEnqueue("test-path", new TestMessage("data"), exception, 3, correlationId);

        Assert.True(result);
    }

    [Fact]
    public async Task EnqueueAsync_AddsMessageToQueue()
    {
        var dlq = new DeadLetterQueue<TestMessage>();
        var exception = new InvalidOperationException("Test error");
        var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        await dlq.EnqueueAsync("test-path", new TestMessage("data"), exception, 3, correlationId);

        var readResult = await dlq.TryReadAsync();
        Assert.True(readResult.Success);
        Assert.NotNull(readResult.Message);
    }

    [Fact]
    public async Task TryReadAsync_EmptyQueue_ReturnsUnsuccessfulResult()
    {
        var dlq = new DeadLetterQueue<TestMessage>();

        var result = await dlq.TryReadAsync();

        Assert.False(result.Success);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task TryReadAsync_WithMessage_ReturnsMessageWithAllProperties()
    {
        var dlq = new DeadLetterQueue<TestMessage>();
        var message = new TestMessage("test-data");
        var exception = new InvalidOperationException("Test error");
        var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var path = "test-path";
        var attempts = 3;

        dlq.TryEnqueue(path, message, exception, attempts, correlationId);

        var result = await dlq.TryReadAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Message);

        var deadLetter = result.Message as DeadLetterMessage<TestMessage>;
        Assert.NotNull(deadLetter);
        Assert.Equal(path, deadLetter.Path);
        Assert.Equal(message, deadLetter.Body);
        Assert.Equal(exception, deadLetter.Exception);
        Assert.Equal(attempts, deadLetter.Attempts);
        Assert.Equal(correlationId, deadLetter.CorrelationId);
        Assert.True((DateTimeOffset.UtcNow - deadLetter.FailedAt) < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TryEnqueue_ExceedsCapacity_ReturnsFalse()
    {
        var dlq = new DeadLetterQueue<TestMessage>(capacity: 2);
        var exception = new InvalidOperationException("Test");
        var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        // Enqueue 2 messages to fill capacity
        var result1 = dlq.TryEnqueue("path", new TestMessage("message-1"), exception, 1, correlationId);
        var result2 = dlq.TryEnqueue("path", new TestMessage("message-2"), exception, 1, correlationId);

        // Third message should be rejected — queue is full
        var result3 = dlq.TryEnqueue("path", new TestMessage("message-3"), exception, 1, correlationId);

        Assert.True(result1);
        Assert.True(result2);
        Assert.False(result3);

        // Read messages — should be the original 2 in FIFO order
        var read1 = await dlq.TryReadAsync();
        Assert.True(read1.Success);
        Assert.Equal("message-1", (read1.Message as DeadLetterMessage<TestMessage>)?.Body.Value);

        var read2 = await dlq.TryReadAsync();
        Assert.True(read2.Success);
        Assert.Equal("message-2", (read2.Message as DeadLetterMessage<TestMessage>)?.Body.Value);

        // Queue should be empty
        var read3 = await dlq.TryReadAsync();
        Assert.False(read3.Success);
    }

    [Fact]
    public async Task TryReadAsync_FIFO_ReturnsMessagesInOrder()
    {
        var dlq = new DeadLetterQueue<TestMessage>();
        var exception = new InvalidOperationException("Test");
        var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        dlq.TryEnqueue("path", new TestMessage("first"), exception, 1, correlationId);
        dlq.TryEnqueue("path", new TestMessage("second"), exception, 1, correlationId);
        dlq.TryEnqueue("path", new TestMessage("third"), exception, 1, correlationId);

        var result1 = await dlq.TryReadAsync();
        var msg1 = (result1.Message as DeadLetterMessage<TestMessage>)?.Body.Value;
        Assert.Equal("first", msg1);

        var result2 = await dlq.TryReadAsync();
        var msg2 = (result2.Message as DeadLetterMessage<TestMessage>)?.Body.Value;
        Assert.Equal("second", msg2);

        var result3 = await dlq.TryReadAsync();
        var msg3 = (result3.Message as DeadLetterMessage<TestMessage>)?.Body.Value;
        Assert.Equal("third", msg3);
    }

    [Fact]
    public async Task EnqueueAsync_WithCancellation_ThrowsOperationCanceledException()
    {
        var dlq = new DeadLetterQueue<TestMessage>(capacity: 1);
        var exception = new InvalidOperationException("Test");
        var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var cts = new CancellationTokenSource();

        // Fill the queue
        await dlq.EnqueueAsync("path", new TestMessage("first"), exception, 1, correlationId);

        // Cancel and try to enqueue (should throw)
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await dlq.EnqueueAsync("path", new TestMessage("second"), exception, 1, correlationId, cts.Token);
        });
    }

    private sealed record TestMessage(string Value);
}
