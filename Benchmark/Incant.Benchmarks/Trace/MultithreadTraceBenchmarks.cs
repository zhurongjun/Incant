using BenchmarkDotNet.Attributes;
using Incant.Base.Trace;
using TraceRecorder = Incant.Base.Trace.Trace;

namespace Incant.Benchmarks.Trace;

[MemoryDiagnoser]
[TraceBenchmarkJob(1)]
public class MultithreadTraceBenchmarks
{
    private const int EventsPerWorker = 256;
    private const int WorkerCount = 4;

    [GlobalSetup]
    public void Setup()
    {
        TraceRecorder.Start();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (TraceRecorder.IsRunning)
        {
            TraceRecorder.Stop();
        }
    }

    [Benchmark(OperationsPerInvoke = WorkerCount * EventsPerWorker)]
    public void RecordBatch()
    {
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
}
