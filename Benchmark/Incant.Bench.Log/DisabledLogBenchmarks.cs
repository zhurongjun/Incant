using BenchmarkDotNet.Attributes;
using Incant.Base.Log;
using LogRecorder = Incant.Base.Log.Log;

namespace Incant.Bench.Log;

[MemoryDiagnoser]
[LogBenchmarkJob(1024)]
public class DisabledLogBenchmarks
{
    [GlobalSetup]
    public void Setup()
    {
        if (LogRecorder.IsRunning)
        {
            LogRecorder.Stop();
        }

        LogRecorder.ClearSinks();
        LogRecorder.MinimumLevel = LogLevel.Info;
    }

    [Benchmark(Baseline = true)]
    public void Empty()
    {
    }

    [Benchmark]
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
}

[MemoryDiagnoser]
[LogBenchmarkJob(1024)]
public class FilteredLogBenchmarks
{
    [GlobalSetup]
    public void Setup()
    {
        LogRecorder.MinimumLevel = LogLevel.Info;
        LogRecorder.AddSink(new BenchmarkNullLogSink());
        LogRecorder.Start(new LogOptions());
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (LogRecorder.IsRunning)
        {
            LogRecorder.Stop();
        }
    }

    [Benchmark]
    public void ZeroArguments()
    {
        LogRecorder.Debug("message");
    }

    [Benchmark]
    public void OneArgument()
    {
        LogRecorder.Debug("message {Value}", 42);
    }

    [Benchmark]
    public void FourArguments()
    {
        LogRecorder.Debug("message {A} {B} {C} {D}", 1, 2, 3, 4);
    }
}
