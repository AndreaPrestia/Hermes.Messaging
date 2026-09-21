using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Drives the process-wide <see cref="HermesRuntimeState"/> across the host lifecycle.
/// </summary>
/// <remarks>
/// <see cref="RuntimeState.Ready"/> is set on host start. <see cref="RuntimeState.Stopping"/> is
/// set via <see cref="IHostApplicationLifetime.ApplicationStopping"/>, which fires BEFORE any
/// hosted service's <c>StopAsync</c> — so new publishes are rejected before the subscribers begin
/// draining, regardless of registration order. <see cref="RuntimeState.Stopped"/> is set on
/// <c>ApplicationStopped</c>, after all draining has completed.
/// </remarks>
internal sealed class HermesLifecycle : IHostedService
{
    private readonly HermesRuntimeState _state;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<HermesLifecycle>? _logger;

    public HermesLifecycle(
        HermesRuntimeState state,
        IHostApplicationLifetime appLifetime,
        ILogger<HermesLifecycle>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(appLifetime);
        _state = state;
        _appLifetime = appLifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The runtime lifecycle reaches Ready on host start. Publishability additionally requires
        // startup recovery to be complete (tracked by HermesReadiness) — the two are combined in
        // HermesRuntimeState.IsReady / EnsureReady and in the publish gate, so a publish can never
        // be accepted before recovery completes even though the lifecycle is Ready (HERMES-006 P1).
        _state.TryTransition(RuntimeState.Created, RuntimeState.Starting);

        _appLifetime.ApplicationStopping.Register(() =>
        {
            _state.Set(RuntimeState.Stopping);
            _logger?.LogInformation("Hermes runtime is Stopping — new publishes are rejected");
        });

        _appLifetime.ApplicationStopped.Register(() => _state.Set(RuntimeState.Stopped));

        _state.Set(RuntimeState.Ready);
        _logger?.LogInformation("Hermes runtime lifecycle is Ready (publishability also requires recovery completion)");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Safety net in case ApplicationStopping did not fire (e.g. abrupt stop path).
        if (_state.Current == RuntimeState.Ready)
        {
            _state.Set(RuntimeState.Stopping);
        }
        return Task.CompletedTask;
    }
}
