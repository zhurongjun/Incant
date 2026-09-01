using BenchmarkDotNet.Attributes;
using Incant.Base.Trace;
using TraceRecorder = Incant.Base.Trace.Trace;

namespace Incant.Bench.Trace;

[MemoryDiagnoser]
[TraceBenchmarkJob(128)]
public class EnabledTraceBenchmarks
{
    private readonly EventArguments _arguments = new()
    {
        File = "input.cpp",
        Count = 4,
    };

    [GlobalSetup]
    public void Setup()
    {
        TraceRecorder.Start();
        TraceRecorder.Event(TraceCategory.Build, "warmup");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (TraceRecorder.IsRunning)
        {
            TraceRecorder.Stop();
        }
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

    [Benchmark]
    public void EventSlow()
    {
        TraceRecorder.EventSlow(TraceCategory.Build, "event {@Arguments}", _arguments);
    }

    private sealed class EventArguments
    {
        public string File { get; init; } = string.Empty;

        public int Count { get; init; }
    }
}
