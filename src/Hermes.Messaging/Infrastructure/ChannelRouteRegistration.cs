using System.Threading.Channels;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Ensures a route gets registered during dependency injection resolution.
/// </summary>
/// <typeparam name="T">Message payload type.</typeparam>
public sealed class ChannelRouteRegistration<T>
{
    public ChannelRouteRegistration(
        string path,
        Func<T, IServiceProvider, CancellationToken, Task> handler,
        ChannelRouteTable<T> routes,
        ChannelRegistry registry,
        Action<BoundedChannelOptions>? configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(registry);

        Path = path;
        Handler = handler;

        registry.GetOrCreate<T>(configure);
        routes.Map(path, handler);
    }

    public string Path { get; }

    public Func<T, IServiceProvider, CancellationToken, Task> Handler { get; }
}
