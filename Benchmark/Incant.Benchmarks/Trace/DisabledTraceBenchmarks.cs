using BenchmarkDotNet.Attributes;
using Incant.Base.Trace;
using TraceRecorder = Incant.Base.Trace.Trace;

namespace Incant.Benchmarks.Trace;

[MemoryDiagnoser]
[TraceBenchmarkJob(1024)]
public class DisabledTraceBenchmarks
{
    [GlobalSetup]
    public void Setup()
    {
        if (TraceRecorder.IsRunning)
        {
            TraceRecorder.Stop();
        }
    }

    [Benchmark(Baseline = true)]
    public void Empty()
    {
    }

    [Benchmark]
    public void Scope()
    {
        using TraceScope scope = TraceRecorder.Scope(TraceCategory.Build, "scope");
    }

    [Benchmark]
    public void Event()
    {
        TraceRecorder.Event(TraceCategory.Build, "event");
    }

    [Benchmark]
    public void Counter()
    {
        TraceRecorder.Counter(TraceCategory.Build, "counter", 1L);
    }
}
