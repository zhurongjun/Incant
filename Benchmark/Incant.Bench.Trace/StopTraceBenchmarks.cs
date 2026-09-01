using BenchmarkDotNet.Attributes;
using Incant.Base.Trace;
using TraceRecorder = Incant.Base.Trace.Trace;

namespace Incant.Bench.Trace;

[MemoryDiagnoser]
[TraceBenchmarkJob(1)]
public class StopTraceBenchmarks
{
    private const int EventsPerWorker = 2048;
    private const int WorkerCount = 4;

    [IterationSetup]
    public void Setup()
    {
        TraceRecorder.Start();
        Parallel.For(
            0,
            WorkerCount,
            _ =>
            {
                for (int index = 0; index < EventsPerWorker; ++index)
                {
                    TraceRecorder.Event(TraceCategory.Build, "event");
                }
            });
    }

    [IterationCleanup]
    public void Cleanup()
    {
        if (TraceRecorder.IsRunning)
        {
            TraceRecorder.Stop();
        }
    }

    [Benchmark]
    public TraceCapture Merge()
    {
        return TraceRecorder.Stop();
    }
}
