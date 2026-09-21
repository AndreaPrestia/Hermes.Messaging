using Hermes.Messaging.Infrastructure;

namespace Hermes.Messaging.Tests.Infrastructure;

public class RetryClassifierTests
{
    [Fact]
    public void Transient_Exception_IsRetried()
    {
        Assert.Equal(FailureDisposition.Retry,
            RetryClassifier.Classify(new InvalidOperationException("transient"), shutdownRequested: false));
    }

    [Fact]
    public void NonRetryableException_IsDeadLettered()
    {
        Assert.Equal(FailureDisposition.DeadLetter,
            RetryClassifier.Classify(new NonRetryableException("permanent"), shutdownRequested: false));
    }

    [Fact]
    public void RouteNotFound_IsDeadLettered()
    {
        Assert.Equal(FailureDisposition.DeadLetter,
            RetryClassifier.Classify(new RouteNotFoundException("r", typeof(string)), shutdownRequested: false));
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(NotImplementedException))]
    public void ConfigurationErrors_AreDeadLettered(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.Equal(FailureDisposition.DeadLetter, RetryClassifier.Classify(ex, shutdownRequested: false));
    }

    [Fact]
    public void Cancellation_DuringShutdown_IsShutdown()
    {
        Assert.Equal(FailureDisposition.Shutdown,
            RetryClassifier.Classify(new OperationCanceledException(), shutdownRequested: true));
    }

    [Fact]
    public void Cancellation_NotDuringShutdown_IsRetried()
    {
        // A handler that throws OCE unrelated to shutdown should still be retried.
        Assert.Equal(FailureDisposition.Retry,
            RetryClassifier.Classify(new OperationCanceledException(), shutdownRequested: false));
    }
}
