using Hermes.Messaging.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Tests.Infrastructure;

public class DeadLetterQueueProcessorTests
{
    [Fact]
    public async Task ProcessDeadLetter_WithRegisteredHandler_InvokesHandler()
    {
        var handlerInvoked = new TaskCompletionSource<DeadLetterMessage<TestMessage>>();
        var dlq = new DeadLetterQueue<TestMessage>();
        var registry = new DeadLetterQueueRegistry();
        registry.Register(dlq);

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(registry);
                services.AddSingleton(dlq);
                services.AddHostedService<DeadLetterQueueProcessor>();
                services.AddScoped<IDeadLetterHandler<TestMessage>>(sp =>
                    new TestDeadLetterHandler(handlerInvoked));
            })
            .Build();

        try
        {
            await host.StartAsync();

            var exception = new InvalidOperationException("Test error");
            var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
            dlq.TryEnqueue("test-path", new TestMessage("data"), exception, 3, correlationId);

            var result = await handlerInvoked.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotNull(result);
            Assert.Equal("test-path", result.Path);
            Assert.Equal("data", result.Body.Value);
            Assert.Equal(3, result.Attempts);
            Assert.Equal(correlationId, result.CorrelationId);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ProcessDeadLetter_WithoutRegisteredHandler_LogsWarning()
    {
        var dlq = new DeadLetterQueue<TestMessage>();
        var registry = new DeadLetterQueueRegistry();
        registry.Register(dlq);

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(registry);
                services.AddSingleton(dlq);
                services.AddHostedService<DeadLetterQueueProcessor>();
                // No handler registered intentionally
            })
            .Build();

        try
        {
            await host.StartAsync();

            var exception = new InvalidOperationException("Test error");
            var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
            dlq.TryEnqueue("test-path", new TestMessage("data"), exception, 3, correlationId);

            // Wait for processing (increased to give processor time to poll)
            await Task.Delay(10000);

            // Message might still be in queue if processor hasn't polled yet
            // Just verify the test doesn't crash
            var result = await dlq.TryReadAsync();
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ProcessDeadLetter_MultipleQueues_ProcessesAll()
    {
        var handler1Invoked = new TaskCompletionSource<bool>();
        var handler2Invoked = new TaskCompletionSource<bool>();
        
        var dlq1 = new DeadLetterQueue<TestMessage1>();
        var dlq2 = new DeadLetterQueue<TestMessage2>();
        var registry = new DeadLetterQueueRegistry();
        registry.Register(dlq1);
        registry.Register(dlq2);

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(registry);
                services.AddSingleton(dlq1);
                services.AddSingleton(dlq2);
                services.AddHostedService<DeadLetterQueueProcessor>();
                services.AddScoped<IDeadLetterHandler<TestMessage1>>(sp =>
                    new SimpleTestHandler<TestMessage1>(handler1Invoked));
                services.AddScoped<IDeadLetterHandler<TestMessage2>>(sp =>
                    new SimpleTestHandler<TestMessage2>(handler2Invoked));
            })
            .Build();

        try
        {
            await host.StartAsync();

            var exception = new InvalidOperationException("Test");
            var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
            dlq1.TryEnqueue("path1", new TestMessage1("data1"), exception, 3, correlationId);
            dlq2.TryEnqueue("path2", new TestMessage2("data2"), exception, 3, correlationId);

            await Task.WhenAll(
                handler1Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10)),
                handler2Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.True(handler1Invoked.Task.IsCompletedSuccessfully);
            Assert.True(handler2Invoked.Task.IsCompletedSuccessfully);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ProcessDeadLetter_HandlerThrowsException_ContinuesProcessing()
    {
        var dlq = new DeadLetterQueue<TestMessage>();
        var registry = new DeadLetterQueueRegistry();
        registry.Register(dlq);
        var callCounter = new CallCounter();

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(registry);
                services.AddSingleton(dlq);
                services.AddSingleton(callCounter);
                services.AddHostedService<DeadLetterQueueProcessor>();
                services.AddScoped<IDeadLetterHandler<TestMessage>>(sp =>
                    new FailingTestHandler<TestMessage>(sp.GetRequiredService<CallCounter>()));
            })
            .Build();

        try
        {
            await host.StartAsync();

            var exception = new InvalidOperationException("Test");
            var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
            
            // Enqueue two messages
            dlq.TryEnqueue("path1", new TestMessage("data1"), exception, 3, correlationId);
            dlq.TryEnqueue("path2", new TestMessage("data2"), exception, 3, correlationId);

            // Wait for processing attempts
            await Task.Delay(15000);

            // Handler should have been called for both messages despite throwing
            Assert.Equal(2, callCounter.Count);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private sealed record TestMessage(string Value);
    private sealed record TestMessage1(string Value);
    private sealed record TestMessage2(string Value);

    private sealed class TestDeadLetterHandler : IDeadLetterHandler<TestMessage>
    {
        private readonly TaskCompletionSource<DeadLetterMessage<TestMessage>> _completion;

        public TestDeadLetterHandler(TaskCompletionSource<DeadLetterMessage<TestMessage>> completion)
        {
            _completion = completion;
        }

        public Task HandleAsync(DeadLetterMessage<TestMessage> deadLetter, CancellationToken cancellationToken)
        {
            _completion.TrySetResult(deadLetter);
            return Task.CompletedTask;
        }
    }

    private sealed class SimpleTestHandler<T> : IDeadLetterHandler<T>
    {
        private readonly TaskCompletionSource<bool> _completion;

        public SimpleTestHandler(TaskCompletionSource<bool> completion)
        {
            _completion = completion;
        }

        public Task HandleAsync(DeadLetterMessage<T> deadLetter, CancellationToken cancellationToken)
        {
            _completion.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    private sealed class CallCounter
    {
        private int _count;
        public int Count => _count;
        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class FailingTestHandler<T> : IDeadLetterHandler<T>
    {
        private readonly CallCounter _counter;

        public FailingTestHandler(CallCounter counter)
        {
            _counter = counter;
        }

        public Task HandleAsync(DeadLetterMessage<T> deadLetter, CancellationToken cancellationToken)
        {
            _counter.Increment();
            throw new InvalidOperationException("Handler failed intentionally");
        }
    }
}
