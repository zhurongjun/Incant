using System.Text.Json;
using Incant.Base.Trace;
using TraceRecorder = Incant.Base.Trace.Trace;

namespace Incant.UnitTests.Trace;

[Collection(TraceCollection.Name)]
public sealed class TraceRecordingTests : TraceTestBase
{
    [Fact]
    public void InstantEventsPreserveEveryVisibilityScope()
    {
        TraceRecorder.Start();
        TraceRecorder.Event(TraceCategory.General, "thread", TraceInstantScope.Thread);
        TraceRecorder.Event(TraceCategory.General, "process", TraceInstantScope.Process);
        TraceRecorder.Event(TraceCategory.General, "global", TraceInstantScope.Global);

        TraceCapture capture = TraceRecorder.Stop();

        AssertInstantScope(capture, "thread", TraceInstantScope.Thread);
        AssertInstantScope(capture, "process", TraceInstantScope.Process);
        AssertInstantScope(capture, "global", TraceInstantScope.Global);
    }

    [Fact]
    public void CountersPreserveNumericKindsAndBoundaryValues()
    {
        TraceRecorder.Start();
        TraceRecorder.Counter(TraceCategory.General, "signed", long.MinValue);
        TraceRecorder.Counter(TraceCategory.General, "unsigned", ulong.MaxValue);
        TraceRecorder.Counter(TraceCategory.General, "floating", double.MaxValue);

        TraceCapture capture = TraceRecorder.Stop();

        TraceCounterValue signed = FindEvent(capture, "signed").CounterValue;
        Assert.Equal(TraceCounterValueKind.Signed, signed.Kind);
        Assert.Equal(long.MinValue, signed.SignedValue);

        TraceCounterValue unsigned = FindEvent(capture, "unsigned").CounterValue;
        Assert.Equal(TraceCounterValueKind.Unsigned, unsigned.Kind);
        Assert.Equal(ulong.MaxValue, unsigned.UnsignedValue);

        TraceCounterValue floating = FindEvent(capture, "floating").CounterValue;
        Assert.Equal(TraceCounterValueKind.FloatingPoint, floating.Kind);
        Assert.Equal(double.MaxValue, floating.FloatingPointValue);
    }

    [Fact]
    public void AsyncAndFlowEventsPreserveKindsAndLargeCorrelationIds()
    {
        const ulong CorrelationId = 0xfedcba9876543210UL;
        TraceRecorder.Start();
        TraceRecorder.AsyncBegin(TraceCategory.Scheduler, "async begin", CorrelationId);
        TraceRecorder.AsyncEvent(TraceCategory.Scheduler, "async event", CorrelationId);
        TraceRecorder.AsyncEnd(TraceCategory.Scheduler, "async end", CorrelationId);
        TraceRecorder.FlowStart(TraceCategory.Dependency, "flow start", CorrelationId);
        TraceRecorder.FlowStep(TraceCategory.Dependency, "flow step", CorrelationId);
        TraceRecorder.FlowEnd(TraceCategory.Dependency, "flow end", CorrelationId);

        TraceCapture capture = TraceRecorder.Stop();

        AssertKindAndId(capture, "async begin", TraceEventKind.AsyncBegin, CorrelationId);
        AssertKindAndId(capture, "async event", TraceEventKind.AsyncEvent, CorrelationId);
        AssertKindAndId(capture, "async end", TraceEventKind.AsyncEnd, CorrelationId);
        AssertKindAndId(capture, "flow start", TraceEventKind.FlowStart, CorrelationId);
        AssertKindAndId(capture, "flow step", TraceEventKind.FlowStep, CorrelationId);
        AssertKindAndId(capture, "flow end", TraceEventKind.FlowEnd, CorrelationId);
    }

    [Fact]
    public void HighVolumeCapturePreservesEveryEventInCallOrder()
    {
        const int EventCount = 5000;
        TraceRecorder.Start();
        for (int index = 0; index < EventCount; ++index)
        {
            TraceRecorder.Event(TraceCategory.General, $"event-{index}");
        }

        TraceCapture capture = TraceRecorder.Stop();

        Assert.Equal(EventCount, capture.Events.Length);
        Assert.Equal(
            Enumerable.Range(0, EventCount).Select(index => $"event-{index}"),
            capture.Events.ToArray().Select(traceEvent => traceEvent.Name));
    }

    [Fact]
    public void SlowPropertiesSupportCaptureHintsJsonShapesAndImmediateSnapshots()
    {
        const string StructuredTemplate = "Compiled {@Input} with {Count} files; summary {$Summary}";
        const string DefaultTemplate = "Default object {Input}";
        const string ArrayTemplate = "Array {Value}";
        const string NullTemplate = "Null {Value}";
        var arguments = new MutableArguments
        {
            Name = "before",
            Values = [1, 2],
        };

        TraceRecorder.Start();
        TraceRecorder.EventSlow(TraceCategory.General, StructuredTemplate, arguments, 2, arguments);
        TraceRecorder.EventSlow(TraceCategory.General, DefaultTemplate, arguments);
        TraceRecorder.EventSlow(TraceCategory.General, ArrayTemplate, new[] { "a", "b" });
        TraceRecorder.EventSlow(TraceCategory.General, NullTemplate, null);

        arguments.Name = "after";
        arguments.Values.Add(3);
        TraceCapture capture = TraceRecorder.Stop();

        JsonElement structured = GetArguments(capture, StructuredTemplate);
        JsonElement input = structured.GetProperty("Input");
        Assert.Equal(TraceInstantScope.Thread, FindEvent(capture, StructuredTemplate).InstantScope);
        Assert.Equal("before", input.GetProperty("Name").GetString());
        Assert.Equal([1, 2], input.GetProperty("Values").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(2, structured.GetProperty("Count").GetInt32());
        Assert.Equal("before:1,2", structured.GetProperty("Summary").GetString());
        Assert.Equal("before:1,2", GetArguments(capture, DefaultTemplate).GetProperty("Input").GetString());
        Assert.Equal(
            ["a", "b"],
            GetArguments(capture, ArrayTemplate).GetProperty("Value").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(JsonValueKind.Null, GetArguments(capture, NullTemplate).GetProperty("Value").ValueKind);
    }

    [Fact]
    public void SlowTemplatesSupportEscapedBracesFormatsAndPositionalProperties()
    {
        const string NamedTemplate = "Literal {{brace}} took {Elapsed,6:000} ms for {Build.Project}";
        const string PositionalTemplate = "Moved {1} before {0} and repeated {1}";
        const string MixedTemplate = "Mixed {0} and {Name}";
        TraceRecorder.Start();
        TraceRecorder.EventSlow(TraceCategory.General, NamedTemplate, 7, "Incant");
        TraceRecorder.EventSlow(TraceCategory.General, PositionalTemplate, "zero", "one");
        TraceRecorder.EventSlow(TraceCategory.General, MixedTemplate, 3, "three");

        TraceCapture capture = TraceRecorder.Stop();

        JsonElement named = GetArguments(capture, NamedTemplate);
        Assert.Equal(7, named.GetProperty("Elapsed").GetInt32());
        Assert.Equal("Incant", named.GetProperty("Build.Project").GetString());

        JsonElement positional = GetArguments(capture, PositionalTemplate);
        Assert.Equal("zero", positional.GetProperty("0").GetString());
        Assert.Equal("one", positional.GetProperty("1").GetString());

        JsonElement mixed = GetArguments(capture, MixedTemplate);
        Assert.Equal(3, mixed.GetProperty("0").GetInt32());
        Assert.Equal("three", mixed.GetProperty("Name").GetString());
    }

    [Fact]
    public void EverySlowRecordingApiPreservesItsArguments()
    {
        const ulong CorrelationId = 123UL;
        TraceRecorder.Start();

        using (TraceRecorder.ScopeSlow(TraceCategory.Build, "scope {Step}", 1))
        {
        }

        TraceRecorder.EventSlow(TraceCategory.Build, "event {Step}", TraceInstantScope.Process, 2);
        TraceRecorder.CounterSlow(TraceCategory.Build, "counter {Step}", 3);
        TraceRecorder.AsyncBeginSlow(TraceCategory.Build, "async begin {Step}", CorrelationId, 4);
        TraceRecorder.AsyncEventSlow(TraceCategory.Build, "async event {Step}", CorrelationId, 5);
        TraceRecorder.AsyncEndSlow(TraceCategory.Build, "async end {Step}", CorrelationId, 6);
        TraceRecorder.FlowStartSlow(TraceCategory.Build, "flow start {Step}", CorrelationId, 7);
        TraceRecorder.FlowStepSlow(TraceCategory.Build, "flow step {Step}", CorrelationId, 8);
        TraceRecorder.FlowEndSlow(TraceCategory.Build, "flow end {Step}", CorrelationId, 9);
        TraceCapture capture = TraceRecorder.Stop();

        Dictionary<string, int> expectedSteps = new()
        {
            ["scope {Step}"] = 1,
            ["event {Step}"] = 2,
            ["counter {Step}"] = 3,
            ["async begin {Step}"] = 4,
            ["async event {Step}"] = 5,
            ["async end {Step}"] = 6,
            ["flow start {Step}"] = 7,
            ["flow step {Step}"] = 8,
            ["flow end {Step}"] = 9,
        };
        Assert.Equal(expectedSteps.Count, capture.Events.Length);
        foreach ((string name, int step) in expectedSteps)
        {
            Assert.Equal(step, GetArguments(capture, name).GetProperty("Step").GetInt32());
            if (name == "event {Step}")
            {
                Assert.Equal(TraceInstantScope.Process, FindEvent(capture, name).InstantScope);
            }
        }
    }

    [Fact]
    public void InvalidSlowTemplatesAndPropertyCountsThrowWithoutRecordingEvents()
    {
        TraceRecorder.Start();

        Assert.Throws<ArgumentNullException>(
            () => TraceRecorder.EventSlow(TraceCategory.General, null!, 1));
        Assert.Throws<FormatException>(
            () => TraceRecorder.EventSlow(TraceCategory.General, "unclosed {Name", 1));
        Assert.Throws<FormatException>(
            () => TraceRecorder.EventSlow(TraceCategory.General, "unopened Name}", 1));
        Assert.Throws<FormatException>(
            () => TraceRecorder.EventSlow(TraceCategory.General, "invalid {First Name}", 1));
        Assert.Throws<FormatException>(
            () => TraceRecorder.EventSlow(TraceCategory.General, "invalid {Name,+5}", 1));
        Assert.Throws<ArgumentException>(
            () => TraceRecorder.EventSlow(TraceCategory.General, "missing {First} {Second}", 1));
        Assert.Throws<ArgumentException>(
            () => TraceRecorder.EventSlow(TraceCategory.General, "extra {Only}", 1, 2));
        Assert.Throws<ArgumentException>(
            () => TraceRecorder.EventSlow(TraceCategory.General, "missing position {1}", "only"));
        TraceCapture capture = TraceRecorder.Stop();

        Assert.Empty(capture.Events.ToArray());
        Assert.Empty(capture.Threads.ToArray());
    }

    [Fact]
    public void SerializationErrorsPropagateWithoutRecordingAnEvent()
    {
        TraceRecorder.Start();

        Assert.Throws<InvalidOperationException>(
            () => TraceRecorder.EventSlow(
                TraceCategory.General,
                "invalid {@Arguments}",
                new ThrowingArguments()));
        TraceCapture capture = TraceRecorder.Stop();

        Assert.Empty(capture.Events.ToArray());
        Assert.Empty(capture.Threads.ToArray());
    }

    private static void AssertInstantScope(
        TraceCapture capture,
        string name,
        TraceInstantScope expectedScope)
    {
        TraceEvent traceEvent = FindEvent(capture, name);
        Assert.Equal(TraceEventKind.Instant, traceEvent.Kind);
        Assert.Equal(expectedScope, traceEvent.InstantScope);
    }

    private static void AssertKindAndId(
        TraceCapture capture,
        string name,
        TraceEventKind expectedKind,
        ulong expectedId)
    {
        TraceEvent traceEvent = FindEvent(capture, name);
        Assert.Equal(expectedKind, traceEvent.Kind);
        Assert.Equal(expectedId, traceEvent.Id);
    }

    private static TraceEvent FindEvent(TraceCapture capture, string name)
    {
        return capture.Events.ToArray().Single(traceEvent => traceEvent.Name == name);
    }

    private static JsonElement GetArguments(TraceCapture capture, string name)
    {
        TraceEvent traceEvent = FindEvent(capture, name);
        Assert.True(traceEvent.Arguments.HasValue);
        return traceEvent.Arguments.Value;
    }

    private sealed class MutableArguments
    {
        public string Name { get; set; } = string.Empty;

        public List<int> Values { get; set; } = [];

        public override string ToString()
        {
            return $"{Name}:{string.Join(',', Values)}";
        }
    }

    private sealed class ThrowingArguments
    {
        public int Value => throw new InvalidOperationException("Serialization failed.");
    }
}
