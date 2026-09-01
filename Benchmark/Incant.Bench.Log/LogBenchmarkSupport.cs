using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Incant.Base.Log;

namespace Incant.Bench.Log;

[AttributeUsage(AttributeTargets.Class)]
internal sealed class LogBenchmarkJobAttribute : Attribute, IConfigSource
{
    internal LogBenchmarkJobAttribute(long invocationCount)
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

internal sealed class BenchmarkNullLogSink : ILogSink
{
    public LogLevel MinimumLevel => LogLevel.Trace;

    public void Start(LogSinkContext context)
    {
    }

    public void Emit(RenderedLogEvent logEvent)
    {
    }

    public void Flush()
    {
    }

    public void Dispose()
    {
    }
}
