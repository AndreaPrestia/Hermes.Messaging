using System.Diagnostics;
using LiteDB;

namespace Hermes.Messaging.Tests.Infrastructure;

/// <summary>
/// Real child-process crash test for the HERMES-001 durable publish boundary.
///
/// A child process publishes a message (persist-before-signal) and then hard-crashes
/// via Environment.FailFast — this is a genuine crash, NOT a graceful StopAsync. The
/// parent then re-opens the durable store and asserts the accepted message survived.
/// </summary>
[Collection("MessageBus")]
public class CrashRecoveryTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"crash_test_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempPath, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task Publish_Accepted_SurvivesHardProcessCrash()
    {
        Directory.CreateDirectory(_tempPath);

        var crash = await RunHarnessAsync("crash");

        Assert.False(string.IsNullOrWhiteSpace(crash.DbPath), "Harness did not emit DBPATH — it may have failed before publishing.");
        Assert.False(string.IsNullOrWhiteSpace(crash.Collection), "Harness did not emit COLLECTION.");
        Assert.False(string.IsNullOrWhiteSpace(crash.MessageId), "Harness did not emit MESSAGEID — publish was not accepted.");

        var acceptedId = Guid.Parse(crash.MessageId!);

        // A graceful StopAsync would NOT prove crash safety; FailFast guarantees a real crash.
        Assert.NotEqual(0, crash.ExitCode);

        // Re-open the durable store the child wrote to and assert the accepted message survived.
        using var db = new LiteDatabase($"Filename={crash.DbPath};Connection=shared;ReadOnly=true");
        var docs = db.GetCollection(crash.Collection);
        var doc = docs.FindOne(Query.EQ("MessageId", new BsonValue(acceptedId)));

        Assert.NotNull(doc);
        Assert.Equal(acceptedId, doc["MessageId"].AsGuid);
        // The message was durably committed but not Completed before the crash. Depending on
        // the exact crash timing it is either Pending (not yet claimed) or Processing (claimed);
        // both are recoverable and neither is a loss.
        var status = doc["Status"].AsString;
        Assert.True(status is "Pending" or "Processing", $"Unexpected status '{status}' after crash.");
    }

    [Fact]
    public async Task InterruptedMessage_IsRecoveredAndProcessed_OnRestart()
    {
        Directory.CreateDirectory(_tempPath);

        // 1. Crash after acceptance, leaving the message durable but not Completed.
        var crash = await RunHarnessAsync("crash");
        Assert.False(string.IsNullOrWhiteSpace(crash.MessageId), "Publish was not accepted before crash.");
        Assert.NotEqual(0, crash.ExitCode);
        var acceptedId = Guid.Parse(crash.MessageId!);

        // 2. Restart on the same store with a working handler; startup recovery + re-claim
        //    must process the interrupted message to completion.
        var recover = await RunHarnessAsync("recover");
        Assert.Equal(0, recover.ExitCode);
        Assert.True(recover.Recovered, "Recovery run did not report the message as reprocessed.");

        // 3. The durable record is now Completed.
        using var db = new LiteDatabase($"Filename={crash.DbPath};Connection=shared;ReadOnly=true");
        var docs = db.GetCollection(crash.Collection);
        var doc = docs.FindOne(Query.EQ("MessageId", new BsonValue(acceptedId)));

        Assert.NotNull(doc);
        Assert.Equal("Completed", doc["Status"].AsString);
    }

    private async Task<HarnessResult> RunHarnessAsync(string mode)
    {
        var harnessDll = ResolveHarnessDll();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(harnessDll);
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(_tempPath);

        using var process = Process.Start(psi)!;

        var result = new HarnessResult();
        var stdout = process.StandardOutput;
        string? line;
        while ((line = await stdout.ReadLineAsync()) is not null)
        {
            if (line.StartsWith("DBPATH=", StringComparison.Ordinal))
                result.DbPath = line["DBPATH=".Length..];
            else if (line.StartsWith("COLLECTION=", StringComparison.Ordinal))
                result.Collection = line["COLLECTION=".Length..];
            else if (line.StartsWith("MESSAGEID=", StringComparison.Ordinal))
                result.MessageId = line["MESSAGEID=".Length..];
            else if (line.StartsWith("RECOVERED=", StringComparison.Ordinal))
                result.Recovered = true;
        }

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
        result.ExitCode = process.ExitCode;
        return result;
    }

    private sealed class HarnessResult
    {
        public string? DbPath { get; set; }
        public string? Collection { get; set; }
        public string? MessageId { get; set; }
        public bool Recovered { get; set; }
        public int ExitCode { get; set; }
    }

    private static string ResolveHarnessDll()
    {
        // tests/Hermes.Messaging.Tests/bin/<cfg>/net10.0/  ->  repo root
        var testBin = AppContext.BaseDirectory;
        var configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        var candidate = Path.GetFullPath(Path.Combine(
            testBin,
            "..", "..", "..", "..", "..",
            "tests", "Hermes.Messaging.CrashHarness", "bin", configuration, "net10.0",
            "Hermes.Messaging.CrashHarness.dll"));

        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException(
                $"Crash harness not found at '{candidate}'. Build the solution before running this test.");
        }

        return candidate;
    }
}
