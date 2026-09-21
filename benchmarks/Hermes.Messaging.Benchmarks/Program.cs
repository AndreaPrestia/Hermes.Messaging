using BenchmarkDotNet.Running;
using Hermes.Messaging.Benchmarks;

// Hermes.Messaging benchmark baseline (HERMES 0.3.0-alpha prep).
//
// Run all:
//   dotnet run -c Release --project benchmarks/Hermes.Messaging.Benchmarks
// Run a subset (filter by class/method):
//   dotnet run -c Release --project benchmarks/Hermes.Messaging.Benchmarks -- --filter *PublishBenchmarks*
//
// Backlog-recovery benchmarks are long-running; run them explicitly:
//   dotnet run -c Release --project benchmarks/Hermes.Messaging.Benchmarks -- --filter *BacklogRecoveryBenchmarks*
//
// Backlog recovery single-shot probe (fast, deterministic wall-clock, not BenchmarkDotNet):
//   dotnet run -c Release --project benchmarks/Hermes.Messaging.Benchmarks -- --backlog-probe 1000 10000 50000
if (args.Length > 0 && args[0] == "--backlog-probe")
{
    var sizes = args.Skip(1).Select(int.Parse).DefaultIfEmpty(1000).ToArray();
    await BacklogProbe.RunAsync(sizes);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

internal sealed partial class Program;
