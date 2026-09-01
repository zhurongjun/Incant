using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Incant.Base.Trace;
using TraceRecorder = Incant.Base.Trace.Trace;

namespace Incant.Bench.Trace;

[MemoryDiagnoser]
[ShortRunJob]
public class GoogleTraceJsonBenchmarks
{
    private TraceCapture _capture = null!;

    [GlobalSetup]
    public void Setup()
    {
        TraceRecorder.Start();
        for (int index = 0; index < 4096; ++index)
        {
            TraceRecorder.Event(TraceCategory.Build, "event");
        }

        _capture = TraceRecorder.Stop();
    }

    [Benchmark]
    public int Write()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        GoogleTraceJsonWriter.Write(writer, _capture);
        writer.Flush();
        return buffer.WrittenCount;
    }
}
