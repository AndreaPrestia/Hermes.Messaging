namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// How a failed processing attempt should be treated by the retry engine.
/// </summary>
internal enum FailureDisposition
{
    /// <summary>Transient failure — schedule another attempt (subject to the attempt limit).</summary>
    Retry,

    /// <summary>Non-retryable programming/configuration failure — dead-letter immediately.</summary>
    DeadLetter,

    /// <summary>Shutdown cancellation — not a real failure; leave for recovery on restart.</summary>
    Shutdown
}

/// <summary>
/// Marker exception a handler can throw to signal a permanent, non-retryable failure.
/// Hermes dead-letters these immediately without consuming further attempts.
/// </summary>
public sealed class NonRetryableException : Exception
{
    public NonRetryableException(string message) : base(message) { }
    public NonRetryableException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Classifies a handler exception into a <see cref="FailureDisposition"/>.
/// </summary>
internal static class RetryClassifier
{
    /// <summary>
    /// Classifies <paramref name="exception"/>. Shutdown cancellation is only reported when the
    /// supplied <paramref name="shutdownRequested"/> flag is set (so a handler that throws its own
    /// <see cref="OperationCanceledException"/> during normal operation is still retried).
    /// </summary>
    public static FailureDisposition Classify(Exception exception, bool shutdownRequested)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (shutdownRequested && exception is OperationCanceledException)
        {
            return FailureDisposition.Shutdown;
        }

        return exception switch
        {
            // Configuration / programming errors are not retryable.
            NonRetryableException => FailureDisposition.DeadLetter,
            RouteNotFoundException => FailureDisposition.DeadLetter,
            ArgumentException => FailureDisposition.DeadLetter,
            NotSupportedException => FailureDisposition.DeadLetter,
            NotImplementedException => FailureDisposition.DeadLetter,
            _ => FailureDisposition.Retry
        };
    }
}
