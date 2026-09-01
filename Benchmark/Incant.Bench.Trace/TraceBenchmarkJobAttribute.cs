using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace Incant.Bench.Trace;

[AttributeUsage(AttributeTargets.Class)]
internal sealed class TraceBenchmarkJobAttribute : Attribute, IConfigSource
{
    internal TraceBenchmarkJobAttribute(long invocationCount)
    {
        Config = ManualConfig
            .CreateEmpty()
            .AddJob(
                Job.ShortRun
                    .WithInvocationCount(invocationCount)
                    .WithUnrollFactor(1));
    }

    public IConfig Config { get; }
}
