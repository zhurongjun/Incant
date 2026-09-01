using BenchmarkDotNet.Attributes;
using Incant.Base.Log;
using LogRecorder = Incant.Base.Log.Log;

namespace Incant.Bench.Log;

[MemoryDiagnoser]
[LogBenchmarkJob(256)]
public class EnabledLogBenchmarks
{
    private readonly StructuredArguments _structured = new()
    {
        File = "input.cpp",
        Count = 4,
    };

    [GlobalSetup]
    public void Setup()
    {
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(new BenchmarkNullLogSink());
        LogRecorder.Start(
            new LogOptions
            {
                QueueCapacityPerThread = 65_536,
                FlushInterval = TimeSpan.FromSeconds(30),
            });
        LogRecorder.Info("warmup");
        LogRecorder.Flush();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (LogRecorder.IsRunning)
        {
            LogRecorder.Stop();
        }
    }

    [Benchmark(Baseline = true)]
    public void ZeroArguments()
    {
        LogRecorder.Info("message");
    }

    [Benchmark]
    public void OneArgument()
    {
        LogRecorder.Info("message {Value}", 42);
    }

    [Benchmark]
    public void FourArguments()
    {
        LogRecorder.Info("message {A} {B} {C} {D}", 1, 2, 3, 4);
    }

    [Benchmark]
    public void DecoratedArgument()
    {
        LogRecorder.Info("message {Path}", Param.Label("input.cpp"));
    }

    [Benchmark]
    public void StructuredArgument()
    {
        LogRecorder.Info("message {Arguments}", Param.Structured(_structured));
    }

    private sealed class StructuredArguments
    {
        public string File { get; init; } = string.Empty;

        public int Count { get; init; }
    }
}
