using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Infrastructure;

public static class ChannelSubscriptionExtensions
{
    /// <summary>
    /// Registers a channel subscription and returns a <see cref="ChannelSubscriptionBuilder{T}"/>
    /// for optional fluent configuration (e.g. dead-letter handler).
    /// </summary>
    public static ChannelSubscriptionBuilder<T> Subscribe<T>(
        this IHostApplicationBuilder builder,
        string path,
        Func<T, IServiceProvider, CancellationToken, Task> handler,
        Action<BoundedChannelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Services.AddSubscription(path, handler, configure);
    }

    /// <summary>
    /// Registers a channel subscription on <see cref="IServiceCollection"/> and returns a
    /// <see cref="ChannelSubscriptionBuilder{T}"/> for optional fluent configuration.
    /// </summary>
    public static ChannelSubscriptionBuilder<T> AddSubscription<T>(
        this IServiceCollection services,
        string path,
        Func<T, IServiceProvider, CancellationToken, Task> handler,
        Action<BoundedChannelOptions>? configure = null)
    {
        services.AddChannelSubscription(path, handler, configure);
        return new ChannelSubscriptionBuilder<T>(services);
    }

    public static IHostApplicationBuilder SubscribeAsync<T>(
        this IHostApplicationBuilder builder,
        string path,
        Func<T, IServiceProvider, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddChannelSubscription(path, handler);
        return builder;
    }

    public static IHostApplicationBuilder SubscribeAsync<T>(
        this IHostApplicationBuilder builder,
        string path,
        Func<T, IServiceProvider, CancellationToken, Task> handler,
        Action<BoundedChannelOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddChannelSubscription(path, handler, configure);
        return builder;
    }

    public static IServiceCollection AddChannelSubscription<T>(
        this IServiceCollection services,
        string path,
        Func<T, IServiceProvider, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddChannelSubscription(path, handler, null);
    }

    public static IServiceCollection AddChannelSubscription<T>(
        this IServiceCollection services,
        string path,
        Func<T, IServiceProvider, CancellationToken, Task> handler,
        Action<BoundedChannelOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(handler);

        EnsureInfrastructure<T>(services);

        services.AddSingleton(provider =>
        {
            var routes = provider.GetRequiredService<ChannelRouteTable<T>>();
            var registry = provider.GetRequiredService<ChannelRegistry>();
            return new ChannelRouteRegistration<T>(path, handler, routes, registry, configure);
        });

        return services;
    }

    private static void EnsureInfrastructure<T>(IServiceCollection services)
    {
        services.TryAddSingleton<ChannelRegistry>();
        services.TryAddSingleton<ChannelRouteTable<T>>();
        services.TryAddSingleton<DeadLetterQueueRegistry>();

        // Auto-register DeadLetterQueue<T> so the subscriber never fails at startup
        services.TryAddSingleton(sp =>
        {
            var queue = new DeadLetterQueue<T>();
            var registry = sp.GetRequiredService<DeadLetterQueueRegistry>();
            registry.Register(queue);
            return queue;
        });

        // Register PersistentMessageStore with deterministic path (no GUID)
        // so the same database is reused across restarts for crash recovery.
        // Use FullName to avoid collisions between types with the same Name in different namespaces.
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetService<MessageBusOptions>();
            var basePath = options?.PersistenceBasePath
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Hermes",
                    "Messaging");
            Directory.CreateDirectory(basePath);

            var safeTypeName = (typeof(T).FullName ?? typeof(T).Name).Replace('.', '_').Replace('+', '_');
            var dbPath = Path.Combine(basePath, $"{safeTypeName}.db");
            return new PersistentMessageStore<T>(dbPath, completedRetention: TimeSpan.FromDays(7));
        });

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PersistentChannelRouterSubscriber<T>>());
    }
}
