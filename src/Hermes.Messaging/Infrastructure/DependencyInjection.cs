using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hermes.Messaging;

public static class DependencyInjection
{
    /// <summary>
    /// Adds the Hermes Messaging infrastructure to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHermesMessaging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register core singletons
        services.TryAddSingleton<ChannelRegistry>();
        services.TryAddSingleton<IMessageBus, InMemoryMessageBus>();
        services.TryAddSingleton<DeadLetterQueueRegistry>();
        services.TryAddSingleton<MessageBusOptions>();
        services.TryAddSingleton<IMessageBusDiagnostics, MessageBusDiagnostics>();
        services.TryAddSingleton<HermesRuntimeState>();
        services.TryAddSingleton<HermesReadiness>();
        services.TryAddSingleton<HermesStoreMetrics>();
        services.TryAddSingleton(TimeProvider.System);

        // Runtime lifecycle: flips Ready on start; Stopping on ApplicationStopping (before any
        // StopAsync drain); Stopped on ApplicationStopped.
        services.AddHostedService<HermesLifecycle>();

        services.AddHostedService<DeadLetterQueueProcessor>();

        // Persistence is always enabled — ensure base path exists
        EnsurePersistenceBasePath(null);

        return services;
    }

    /// <summary>
    /// Adds the Hermes Messaging infrastructure with configuration action.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configuration action for message bus options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHermesMessaging(
        this IServiceCollection services,
        Action<MessageBusOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new MessageBusOptions();
        configure(options);

        // Replace any previously registered defaults with the configured instances.
        // Using Replace ensures that even if AddHermesMessaging() was called first,
        // the configured options and registry take precedence.
        services.Replace(ServiceDescriptor.Singleton(options));
        services.Replace(ServiceDescriptor.Singleton(new ChannelRegistry(options.DefaultChannelCapacity)));

        services.AddHermesMessaging();

        // Persistence is always enabled — ensure configured base path exists
        EnsurePersistenceBasePath(options.PersistenceBasePath);

        return services;
    }

    private static void EnsurePersistenceBasePath(string? basePath)
    {
        var persistencePath = basePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hermes",
            "Messaging");
        Directory.CreateDirectory(persistencePath);
    }
}

/// <summary>
/// Options for configuring the Hermes Messaging bus.
/// </summary>
public sealed class MessageBusOptions
{
    /// <summary>
    /// Gets or sets the default channel capacity.
    /// Default is 10,000.
    /// </summary>
    public int DefaultChannelCapacity { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets the maximum number of <b>total handler invocations</b> before a message is
    /// dead-lettered — this includes the first attempt. For example, <c>MaxAttempts = 1</c>
    /// dead-letters after the first failure (no retries); <c>MaxAttempts = 3</c> allows exactly
    /// three handler invocations. Default is 3.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Gets or sets the initial retry delay in milliseconds.
    /// Default is 100ms.
    /// </summary>
    public int InitialRetryDelayMs { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum number of messages processed concurrently per message type.
    /// Default is 1 (sequential processing). Set higher for I/O-bound handlers.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    /// Gets or sets the base path for message persistence storage.
    /// </summary>
    public string? PersistenceBasePath { get; set; }

    /// <summary>
    /// Maximum time to wait for in-flight handlers to finish during graceful shutdown.
    /// Work not completed within this window is left durable for restart. Default is 30s.
    /// </summary>
    public TimeSpan ShutdownGracePeriod { get; set; } = TimeSpan.FromSeconds(30);
}
