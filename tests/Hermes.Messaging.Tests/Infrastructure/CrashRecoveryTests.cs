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
        var harnessDll = ResolveHarnessDll();
        Directory.CreateDirectory(_tempPath);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(harnessDll);
        psi.ArgumentList.Add(_tempPath);

        using var process = Process.Start(psi)!;

        string? dbPath = null;
        string? collection = null;
        string? messageId = null;

        // Read stdout until we have the acceptance markers or the process exits.
        var stdout = process.StandardOutput;
        string? line;
        while ((line = await stdout.ReadLineAsync()) is not null)
        {
            if (line.StartsWith("DBPATH=", StringComparison.Ordinal))
                dbPath = line["DBPATH=".Length..];
            else if (line.StartsWith("COLLECTION=", StringComparison.Ordinal))
                collection = line["COLLECTION=".Length..];
            else if (line.StartsWith("MESSAGEID=", StringComparison.Ordinal))
            {
                messageId = line["MESSAGEID=".Length..];
                break;
            }
        }

        // Wait for the hard crash to fully terminate the process (releases the LiteDB file lock).
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(string.IsNullOrWhiteSpace(dbPath), "Harness did not emit DBPATH — it may have failed before publishing.");
        Assert.False(string.IsNullOrWhiteSpace(collection), "Harness did not emit COLLECTION.");
        Assert.False(string.IsNullOrWhiteSpace(messageId), "Harness did not emit MESSAGEID — publish was not accepted.");

        var acceptedId = Guid.Parse(messageId!);

        // A graceful StopAsync would NOT prove crash safety; FailFast guarantees a real crash.
        Assert.NotEqual(0, process.ExitCode);

        // Re-open the durable store the child wrote to and assert the accepted message survived.
        using var db = new LiteDatabase($"Filename={dbPath};Connection=shared;ReadOnly=true");
        var docs = db.GetCollection(collection);
        var doc = docs.FindOne(Query.EQ("MessageId", new BsonValue(acceptedId)));

        Assert.NotNull(doc);
        Assert.Equal(acceptedId, doc["MessageId"].AsGuid);
        // The message was durably committed but never completed before the crash.
        // LiteDB serializes the MessageStatus enum by name.
        Assert.Equal("Pending", doc["Status"].AsString);
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
