using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace Incant.Bench.Deps;

[AttributeUsage(AttributeTargets.Class)]
internal sealed class DepsBenchmarkJobAttribute : Attribute, IConfigSource
{
    internal DepsBenchmarkJobAttribute()
    {
        Config = ManualConfig.CreateEmpty().AddJob(
            Job.ShortRun.WithInvocationCount(1).WithUnrollFactor(1));
    }

    public IConfig Config { get; }
}
