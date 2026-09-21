using System.Diagnostics;
using System.Diagnostics.Metrics;
using Hermes.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Tests.Infrastructure;

/// <summary>
/// HERMES-005 — observability, readiness and schema version.
/// </summary>
[Collection("MessageBus")]
public class ObservabilityTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"obs_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempPath, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private IHost BuildHost(Func<TestMessage, IServiceProvider, CancellationToken, Task> handler)
        => Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                services.AddChannelSubscription<TestMessage>("obs/route", handler);
            })
            .Build();

    [Fact]
    public async Task Diagnostics_IsReady_TrueOnlyAfterStart()
    {
        using var host = BuildHost((_, _, _) => Task.CompletedTask);
        var diag = host.Services.GetRequiredService<IMessageBusDiagnostics>();

        Assert.False(diag.IsReady); // not started yet

        await host.StartAsync();
        try
        {
            // Ready requires runtime Ready + recovery complete for all subscribers.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!diag.IsReady && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.True(diag.IsReady);
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.False(diag.IsReady); // no longer Ready after stop
    }

    [Fact]
    public async Task Publish_EmitsPublishSpan()
    {
        var spans = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == HermesTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => { lock (spans) spans.Add(a.OperationName); }
        };
        ActivitySource.AddActivityListener(listener);

        using var host = BuildHost((_, _, _) => Task.CompletedTask);
        await host.StartAsync();
        try
        {
            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.PublishAsync("obs/route", new TestMessage("x"));

            // Allow the Process span to also fire.
            await Task.Delay(300);

            lock (spans)
            {
                Assert.Contains("Publish", spans);
                Assert.Contains("Process", spans);
            }
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Publish_EmitsPublishedAndPersistedCounters()
    {
        long published = 0, persisted = 0;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HermesTelemetry.Name &&
                    instrument.Name is "messages.published" or "messages.persisted")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "messages.published") Interlocked.Add(ref published, value);
            else if (instrument.Name == "messages.persisted") Interlocked.Add(ref persisted, value);
        });
        meterListener.Start();

        using var host = BuildHost((_, _, _) => Task.CompletedTask);
        await host.StartAsync();
        try
        {
            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.PublishAsync("obs/route", new TestMessage("x"));

            Assert.Equal(1, Interlocked.Read(ref published));
            Assert.Equal(1, Interlocked.Read(ref persisted));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public void PersistedMessage_HasCurrentSchemaVersion()
    {
        var dbPath = Path.Combine(_tempPath, "schema.db");
        Directory.CreateDirectory(_tempPath);
        using var store = new PersistentMessageStore<TestMessage>(dbPath);

        var id = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        store.Insert(new ChannelMessage<TestMessage>("r", new TestMessage("x"), Guid.CreateVersion7(DateTimeOffset.UtcNow), id));

        var persisted = store.GetByMessageId(id);
        Assert.NotNull(persisted);
        Assert.Equal(PersistedMessageSchema.CurrentVersion, persisted.SchemaVersion);
    }

    private sealed record TestMessage(string Value);
}
