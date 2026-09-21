using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Stores the route handlers for a specific message type.
/// </summary>
/// <typeparam name="T">Message payload type.</typeparam>
internal sealed class ChannelRouteTable<T>
{
    private readonly ConcurrentDictionary<string, Func<T, IServiceProvider, CancellationToken, Task>> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ChannelRouteTable<T>>? _logger;

    public ChannelRouteTable(ILogger<ChannelRouteTable<T>>? logger = null)
    {
        _logger = logger;
    }

    public void Map(string path, Func<T, IServiceProvider, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!_routes.TryAdd(path, handler))
        {
            throw new InvalidOperationException($"Route '{path}' already registered for message type '{typeof(T).Name}'.");
        }
    }

    /// <summary>
    /// Returns true if a handler is registered for the given route (case-insensitive).
    /// Used to validate a route before durable acceptance.
    /// </summary>
    public bool HasRoute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _routes.ContainsKey(path);
    }

    public Task DispatchAsync(string path, T message, IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!_routes.TryGetValue(path, out var handler))
        {
            _logger?.LogWarning("No handler registered for route '{Path}' on message type '{MessageType}'.", path, typeof(T).Name);
            ChannelMetrics.RecordDropped(typeof(T), path);
            throw new RouteNotFoundException(path, typeof(T));
        }

        return handler(message, services, ct);
    }
}

/// <summary>
/// Thrown when a message is published to a route that has no registered handler.
/// This is a configuration error and should not be retried.
/// </summary>
public sealed class RouteNotFoundException : InvalidOperationException
{
    public RouteNotFoundException(string route, Type messageType)
        : base($"No handler registered for route '{route}' on message type '{messageType.Name}'.")
    {
        Route = route;
        MessageType = messageType;
    }

    public string Route { get; }
    public Type MessageType { get; }
}
