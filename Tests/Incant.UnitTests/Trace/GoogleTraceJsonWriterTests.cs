using System.Buffers;
using System.Text.Json;
using Incant.Base.Trace;
using TraceRecorder = Incant.Base.Trace.Trace;

namespace Incant.UnitTests.Trace;

[Collection(TraceCollection.Name)]
public sealed class GoogleTraceJsonWriterTests : TraceTestBase
{
    [Fact]
    public void WriteEmptyCaptureProducesACompleteDocumentAndProcessMetadata()
    {
        TraceRecorder.Start();
        TraceCapture capture = TraceRecorder.Stop();

        using JsonDocument document = WriteCapture(capture);
        JsonElement root = document.RootElement;
        JsonElement metadata = Assert.Single(root.GetProperty("traceEvents").EnumerateArray().ToArray());

        Assert.Equal("ms", root.GetProperty("displayTimeUnit").GetString());
        Assert.Equal("M", metadata.GetProperty("ph").GetString());
        Assert.Equal("process_name", metadata.GetProperty("name").GetString());
        Assert.Equal(capture.ProcessId, metadata.GetProperty("pid").GetInt32());
        Assert.Equal(0, metadata.GetProperty("tid").GetInt32());
        Assert.Equal(capture.ProcessName, metadata.GetProperty("args").GetProperty("name").GetString());
    }

    [Fact]
    public void WriteMapsEventKindsScopesCategoriesAndCorrelationIds()
    {
        const ulong CorrelationId = 0xfedcba9876543210UL;
        const string EscapedName = "quote\" and line\n中文";
        TraceRecorder.Start();

        using (TraceRecorder.Scope(TraceCategory.Build | TraceCategory.IO, EscapedName))
        {
        }

        TraceRecorder.Event(TraceCategory.General, "thread instant", TraceInstantScope.Thread);
        TraceRecorder.Event(TraceCategory.General, "process instant", TraceInstantScope.Process);
        TraceRecorder.Event(TraceCategory.General, "global instant", TraceInstantScope.Global);
        TraceRecorder.AsyncBegin(TraceCategory.Scheduler, "async begin", CorrelationId);
        TraceRecorder.AsyncEvent(TraceCategory.Scheduler, "async event", CorrelationId);
        TraceRecorder.AsyncEnd(TraceCategory.Scheduler, "async end", CorrelationId);
        TraceRecorder.FlowStart(TraceCategory.Dependency, "flow start", CorrelationId);
        TraceRecorder.FlowStep(TraceCategory.Dependency, "flow step", CorrelationId);
        TraceRecorder.FlowEnd(TraceCategory.Dependency, "flow end", CorrelationId);
        TraceCapture capture = TraceRecorder.Stop();

        using JsonDocument document = WriteCapture(capture);
        JsonElement[] events = GetBusinessEvents(document);
        JsonElement complete = FindEvent(events, EscapedName);

        Assert.Equal("X", complete.GetProperty("ph").GetString());
        Assert.Equal("Build,IO", complete.GetProperty("cat").GetString());
        Assert.True(complete.GetProperty("dur").GetDouble() >= 0);
        Assert.False(complete.TryGetProperty("args", out _));
        Assert.Equal("t", FindEvent(events, "thread instant").GetProperty("s").GetString());
        Assert.Equal("p", FindEvent(events, "process instant").GetProperty("s").GetString());
        Assert.Equal("g", FindEvent(events, "global instant").GetProperty("s").GetString());
        AssertPhaseAndId(events, "async begin", "b", CorrelationId);
        AssertPhaseAndId(events, "async event", "n", CorrelationId);
        AssertPhaseAndId(events, "async end", "e", CorrelationId);
        AssertPhaseAndId(events, "flow start", "s", CorrelationId);
        AssertPhaseAndId(events, "flow step", "t", CorrelationId);
        AssertPhaseAndId(events, "flow end", "f", CorrelationId);
        Assert.All(
            events,
            traceEvent =>
            {
                Assert.True(traceEvent.GetProperty("ts").GetDouble() >= 0);
                Assert.Equal(capture.ProcessId, traceEvent.GetProperty("pid").GetInt32());
                Assert.True(traceEvent.GetProperty("tid").GetInt32() > 0);
            });
    }

    [Fact]
    public void WritePreservesCounterBoundariesAndArgumentShapes()
    {
        TraceRecorder.Start();
        TraceRecorder.Counter(TraceCategory.Cache, "signed counter", long.MinValue);
        TraceRecorder.Counter(TraceCategory.Cache, "unsigned counter", ulong.MaxValue);
        TraceRecorder.Counter(TraceCategory.Cache, "floating counter", double.MaxValue);
        TraceRecorder.CounterSlow(
            TraceCategory.Cache,
            "multi counter {Running} {Waiting}",
            2,
            5);
        TraceRecorder.EventSlow(TraceCategory.General, "object args {@Input}", new { Text = "value" });
        TraceRecorder.EventSlow(TraceCategory.General, "scalar args {Value}", 9);
        TraceRecorder.EventSlow(TraceCategory.General, "array args {@Value}", new[] { "a", "b" });
        TraceRecorder.EventSlow(TraceCategory.General, "null args {Value}", null);
        TraceCapture capture = TraceRecorder.Stop();

        using JsonDocument document = WriteCapture(capture);
        JsonElement[] events = GetBusinessEvents(document);

        Assert.Equal(
            long.MinValue,
            FindEvent(events, "signed counter").GetProperty("args").GetProperty("value").GetInt64());
        Assert.Equal(
            ulong.MaxValue,
            FindEvent(events, "unsigned counter").GetProperty("args").GetProperty("value").GetUInt64());
        Assert.Equal(
            double.MaxValue,
            FindEvent(events, "floating counter").GetProperty("args").GetProperty("value").GetDouble());

        JsonElement multiCounter = FindEvent(events, "multi counter {Running} {Waiting}").GetProperty("args");
        Assert.Equal(2, multiCounter.GetProperty("Running").GetInt32());
        Assert.Equal(5, multiCounter.GetProperty("Waiting").GetInt32());
        Assert.Equal(
            "value",
            FindEvent(events, "object args {@Input}")
                .GetProperty("args")
                .GetProperty("Input")
                .GetProperty("Text")
                .GetString());
        Assert.Equal(
            9,
            FindEvent(events, "scalar args {Value}").GetProperty("args").GetProperty("Value").GetInt32());
        Assert.Equal(
            ["a", "b"],
            FindEvent(events, "array args {@Value}")
                .GetProperty("args")
                .GetProperty("Value")
                .EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Equal(
            JsonValueKind.Null,
            FindEvent(events, "null args {Value}").GetProperty("args").GetProperty("Value").ValueKind);
    }

    [Fact]
    public void WriteIncludesMetadataForNamedThreads()
    {
        int workerThreadId = 0;
        var worker = new Thread(
            () =>
            {
                workerThreadId = Environment.CurrentManagedThreadId;
                TraceRecorder.Event(TraceCategory.General, "worker event");
            })
        {
            Name = "Named trace worker",
        };

        TraceRecorder.Start();
        worker.Start();
        worker.Join();
        TraceCapture capture = TraceRecorder.Stop();

        using JsonDocument document = WriteCapture(capture);
        JsonElement[] events = document.RootElement.GetProperty("traceEvents").EnumerateArray().ToArray();
        Assert.Contains(
            events,
            traceEvent =>
                traceEvent.GetProperty("ph").GetString() == "M"
                && traceEvent.GetProperty("name").GetString() == "thread_name"
                && traceEvent.GetProperty("tid").GetInt32() == workerThreadId
                && traceEvent.GetProperty("args").GetProperty("name").GetString() == "Named trace worker");
    }

    [Fact]
    public void WriteDoesNotOwnOrFlushTheJsonWriter()
    {
        TraceRecorder.Start();
        TraceRecorder.Event(TraceCategory.General, "event");
        TraceCapture capture = TraceRecorder.Stop();
        var buffer = new ArrayBufferWriter<byte>(4096);
        using var writer = new Utf8JsonWriter(buffer);

        GoogleTraceJsonWriter.Write(writer, capture);

        Assert.Equal(0, writer.BytesCommitted);
        Assert.True(writer.BytesPending > 0);
        writer.Flush();
        Assert.True(writer.BytesCommitted > 0);
        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        Assert.Equal("ms", document.RootElement.GetProperty("displayTimeUnit").GetString());
    }

    [Fact]
    public void WriteRejectsNullArguments()
    {
        TraceRecorder.Start();
        TraceCapture capture = TraceRecorder.Stop();
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);

        Assert.Throws<ArgumentNullException>(() => GoogleTraceJsonWriter.Write(null!, capture));
        Assert.Throws<ArgumentNullException>(() => GoogleTraceJsonWriter.Write(writer, null!));
    }

    private static JsonDocument WriteCapture(TraceCapture capture)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            GoogleTraceJsonWriter.Write(writer, capture);
            writer.Flush();
        }

        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    private static JsonElement[] GetBusinessEvents(JsonDocument document)
    {
        return document
            .RootElement
            .GetProperty("traceEvents")
            .EnumerateArray()
            .Where(traceEvent => traceEvent.GetProperty("ph").GetString() != "M")
            .ToArray();
    }

    private static JsonElement FindEvent(JsonElement[] events, string name)
    {
        return events.Single(traceEvent => traceEvent.GetProperty("name").GetString() == name);
    }

    private static void AssertPhaseAndId(
        JsonElement[] events,
        string name,
        string expectedPhase,
        ulong expectedId)
    {
        JsonElement traceEvent = FindEvent(events, name);
        Assert.Equal(expectedPhase, traceEvent.GetProperty("ph").GetString());
        Assert.Equal($"0x{expectedId:x}", traceEvent.GetProperty("id").GetString());
    }
}
