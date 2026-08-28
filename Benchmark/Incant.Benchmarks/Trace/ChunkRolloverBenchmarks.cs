using BenchmarkDotNet.Attributes;
using Incant.Base.Trace;
using TraceRecorder = Incant.Base.Trace.Trace;

namespace Incant.Benchmarks.Trace;

[MemoryDiagnoser]
[TraceBenchmarkJob(1)]
public class ChunkRolloverBenchmarks
{
    [IterationSetup]
    public void Setup()
    {
        TraceRecorder.Start();
        for (int index = 0; index < 1024; ++index)
        {
            TraceRecorder.Event(TraceCategory.Build, "fill");
        }
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
    public void Rollover()
    {
        TraceRecorder.Event(TraceCategory.Build, "rollover");
    }
}
