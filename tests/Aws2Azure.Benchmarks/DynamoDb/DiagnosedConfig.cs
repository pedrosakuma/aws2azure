using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using DotnetDiagnostics.BenchmarkDotNet;

namespace Aws2Azure.Benchmarks.DynamoDb;

/// <summary>
/// Config for the EventPipe-sampled translation hotpath probe
/// (<see cref="TranslationHotpathDiagnostics"/>). Uses a
/// <see cref="RunStrategy.Monitoring"/> job so each <c>[Benchmark]</c> body runs
/// as a single multi-second invocation — the stable window the
/// <see cref="DotnetDiagnosticsDiagnoser"/> needs to attach an EventPipe CPU /
/// allocation sampler. The timing column from a Monitoring job is NOT
/// publication-grade; the headline output here is the diagnostics report
/// (<c>*-dotnet-diagnostics-report.md</c>), not the mean.
/// </summary>
public sealed class DiagnosedConfig : ManualConfig
{
    public DiagnosedConfig()
    {
        AddJob(Job.Default
            .WithStrategy(RunStrategy.Monitoring)
            .WithWarmupCount(1)
            .WithIterationCount(2)
            .WithInvocationCount(1)
            .WithUnrollFactor(1)
            .WithId("Diagnose"));

        AddDiagnoser(new DotnetDiagnosticsDiagnoser());
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
