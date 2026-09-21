using Hermes.Messaging.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Benchmarks;

/// <summary>
/// Message payloads of varying size for the publish/processing benchmarks. Payload size is
/// controlled by <see cref="Blob"/> length.
/// </summary>
public sealed record BenchMessage(int Id, string Blob);

internal static class BenchSupport
{
    /// <summary>Creates a payload whose serialized size is roughly <paramref name="approxBytes"/>.</summary>
    public static BenchMessage MakeMessage(int id, int approxBytes)
        => new(id, new string('x', Math.Max(0, approxBytes)));

    /// <summary>Creates a unique temp persistence directory for an isolated bench run.</summary>
    public static string NewTempStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"hermes_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Builds and starts a host with a single subscription and the given handler / concurrency.
    /// </summary>
    public static async Task<IHost> StartHostAsync(
        string persistencePath,
        string route,
        Func<BenchMessage, IServiceProvider, CancellationToken, Task> handler,
        int maxConcurrency = 1)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts =>
                {
                    opts.PersistenceBasePath = persistencePath;
                    opts.MaxConcurrency = maxConcurrency;
                    opts.InitialRetryDelayMs = 1;
                });
                services.AddDeadLetterQueue<BenchMessage>();
                services.AddChannelSubscription<BenchMessage>(route, handler);
            })
            .Build();

        await host.StartAsync();
        return host;
    }
}
