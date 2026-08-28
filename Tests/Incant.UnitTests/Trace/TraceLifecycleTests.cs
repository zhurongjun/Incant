using Incant.Base.Trace;
using TraceRecorder = Incant.Base.Trace.Trace;

namespace Incant.UnitTests.Trace;

[Collection(TraceCollection.Name)]
public sealed class TraceLifecycleTests : TraceTestBase
{
    [Fact]
    public void StartAndStopExposeStateAndCaptureMetadata()
    {
        Assert.False(TraceRecorder.IsRunning);

        TraceRecorder.Start(TraceCategory.Build);

        Assert.True(TraceRecorder.IsRunning);
        Assert.True(TraceRecorder.IsEnabled(TraceCategory.Build));
        Assert.False(TraceRecorder.IsEnabled(TraceCategory.General));

        TraceCapture capture = TraceRecorder.Stop();

        Assert.False(TraceRecorder.IsRunning);
        Assert.Equal(Environment.ProcessId, capture.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(capture.ProcessName));
        Assert.True(capture.TimestampFrequency > 0);
        Assert.True(capture.DurationTicks >= 0);
        Assert.Empty(capture.Events.ToArray());
        Assert.Empty(capture.Threads.ToArray());
    }

    [Fact]
    public void StartingTwiceThrowsWithoutReplacingTheActiveSession()
    {
        TraceRecorder.Start(TraceCategory.Build);

        Assert.Throws<InvalidOperationException>(() => TraceRecorder.Start(TraceCategory.General));
        Assert.True(TraceRecorder.IsRunning);
        Assert.True(TraceRecorder.IsEnabled(TraceCategory.Build));
        Assert.False(TraceRecorder.IsEnabled(TraceCategory.General));

        TraceRecorder.Event(TraceCategory.Build, "original session");
        TraceCapture capture = TraceRecorder.Stop();

        TraceEvent traceEvent = Assert.Single(capture.Events.ToArray());
        Assert.Equal("original session", traceEvent.Name);
    }

    [Fact]
    public void StoppingWithoutASessionThrowsAndDoesNotPreventLaterSessions()
    {
        Assert.Throws<InvalidOperationException>(() => TraceRecorder.Stop());

        TraceRecorder.Start();
        TraceRecorder.Event(TraceCategory.General, "later session");
        TraceCapture capture = TraceRecorder.Stop();

        TraceEvent traceEvent = Assert.Single(capture.Events.ToArray());
        Assert.Equal("later session", traceEvent.Name);
    }

    [Fact]
    public void ConsecutiveCapturesRemainIndependent()
    {
        TraceRecorder.Start();
        TraceRecorder.Event(TraceCategory.General, "first");
        TraceCapture firstCapture = TraceRecorder.Stop();

        TraceRecorder.Start();
        TraceRecorder.Event(TraceCategory.General, "second");
        TraceCapture secondCapture = TraceRecorder.Stop();

        Assert.Equal("first", Assert.Single(firstCapture.Events.ToArray()).Name);
        Assert.Equal("second", Assert.Single(secondCapture.Events.ToArray()).Name);
    }

    [Fact]
    public void CategoryMasksMatchAnyEnabledBit()
    {
        TraceRecorder.Start(TraceCategory.Build | TraceCategory.IO);

        Assert.False(TraceRecorder.IsEnabled(TraceCategory.None));
        Assert.True(TraceRecorder.IsEnabled(TraceCategory.Build));
        Assert.True(TraceRecorder.IsEnabled(TraceCategory.IO));
        Assert.True(TraceRecorder.IsEnabled(TraceCategory.General | TraceCategory.Build));
        Assert.False(TraceRecorder.IsEnabled(TraceCategory.General | TraceCategory.Cache));

        TraceRecorder.Event(TraceCategory.None, "none");
        TraceRecorder.Event(TraceCategory.General, "general");
        TraceRecorder.Event(TraceCategory.Build, "build");
        TraceRecorder.Event(TraceCategory.General | TraceCategory.IO, "combined");
        TraceCapture capture = TraceRecorder.Stop();

        Assert.Equal(["build", "combined"], capture.Events.ToArray().Select(traceEvent => traceEvent.Name));
    }

    [Fact]
    public void AllEnablesEveryDefinedCategory()
    {
        TraceCategory[] categories =
        [
            TraceCategory.General,
            TraceCategory.Build,
            TraceCategory.Dependency,
            TraceCategory.Scheduler,
            TraceCategory.Process,
            TraceCategory.IO,
            TraceCategory.Cache,
        ];

        TraceRecorder.Start(TraceCategory.All);
        foreach (TraceCategory category in categories)
        {
            Assert.True(TraceRecorder.IsEnabled(category));
            TraceRecorder.Event(category, category.ToString());
        }

        TraceCapture capture = TraceRecorder.Stop();

        Assert.Equal(categories, capture.Events.ToArray().Select(traceEvent => traceEvent.Category));
    }

    [Fact]
    public void NoneStartsAValidSessionThatRecordsNothing()
    {
        TraceRecorder.Start(TraceCategory.None);
        TraceRecorder.Event(TraceCategory.General, "ignored");

        TraceCapture capture = TraceRecorder.Stop();

        Assert.Empty(capture.Events.ToArray());
        Assert.Empty(capture.Threads.ToArray());
    }

    [Fact]
    public void DisabledCategoriesSuppressEveryEventKindAndArgumentSerialization()
    {
        const ulong CorrelationId = 17UL;
        var arguments = new ThrowingArguments();
        TraceRecorder.Start(TraceCategory.Build);

        using (TraceRecorder.Scope(TraceCategory.General, "scope"))
        {
        }

        using (TraceRecorder.ScopeSlow(TraceCategory.General, "slow scope {@Arguments}", arguments))
        {
        }

        TraceRecorder.Event(TraceCategory.General, "event");
        TraceRecorder.Counter(TraceCategory.General, "counter", 1L);
        TraceRecorder.AsyncBegin(TraceCategory.General, "async begin", CorrelationId);
        TraceRecorder.AsyncEvent(TraceCategory.General, "async event", CorrelationId);
        TraceRecorder.AsyncEnd(TraceCategory.General, "async end", CorrelationId);
        TraceRecorder.FlowStart(TraceCategory.General, "flow start", CorrelationId);
        TraceRecorder.FlowStep(TraceCategory.General, "flow step", CorrelationId);
        TraceRecorder.FlowEnd(TraceCategory.General, "flow end", CorrelationId);
        TraceRecorder.EventSlow(TraceCategory.General, "slow event {@Arguments}", arguments);
        TraceRecorder.CounterSlow(TraceCategory.General, "slow counter {@Arguments}", arguments);
        TraceRecorder.AsyncBeginSlow(
            TraceCategory.General,
            "slow async begin {@Arguments}",
            CorrelationId,
            arguments);
        TraceRecorder.AsyncEventSlow(
            TraceCategory.General,
            "slow async event {@Arguments}",
            CorrelationId,
            arguments);
        TraceRecorder.AsyncEndSlow(
            TraceCategory.General,
            "slow async end {@Arguments}",
            CorrelationId,
            arguments);
        TraceRecorder.FlowStartSlow(
            TraceCategory.General,
            "slow flow start {@Arguments}",
            CorrelationId,
            arguments);
        TraceRecorder.FlowStepSlow(
            TraceCategory.General,
            "slow flow step {@Arguments}",
            CorrelationId,
            arguments);
        TraceRecorder.FlowEndSlow(
            TraceCategory.General,
            "slow flow end {@Arguments}",
            CorrelationId,
            arguments);
        TraceRecorder.EventSlow(TraceCategory.General, "invalid {");
        TraceRecorder.Event(TraceCategory.Build, "enabled");

        TraceCapture capture = TraceRecorder.Stop();

        TraceEvent traceEvent = Assert.Single(capture.Events.ToArray());
        Assert.Equal("enabled", traceEvent.Name);
    }

    [Fact]
    public void RecordingCallsWithoutASessionAreNoOps()
    {
        using (TraceRecorder.Scope(TraceCategory.Build, "scope"))
        {
        }

        TraceRecorder.Event(TraceCategory.Build, "event");
        TraceRecorder.Counter(TraceCategory.Build, "counter", 1L);
        TraceRecorder.AsyncBegin(TraceCategory.Build, "async", 1UL);
        TraceRecorder.FlowStart(TraceCategory.Build, "flow", 1UL);
        TraceRecorder.EventSlow(
            TraceCategory.Build,
            "slow event {@Arguments}",
            new ThrowingArguments());
        TraceRecorder.EventSlow(TraceCategory.Build, "invalid {");

        TraceRecorder.Start();
        TraceCapture capture = TraceRecorder.Stop();

        Assert.Empty(capture.Events.ToArray());
        Assert.Empty(capture.Threads.ToArray());
    }

    [Fact]
    public void CreateIdReturnsUniqueNonzeroValuesAcrossThreads()
    {
        const int IdCount = 256;
        var ids = new ulong[IdCount];

        Parallel.For(0, IdCount, index => ids[index] = TraceRecorder.CreateId());

        Assert.Equal(IdCount, ids.Distinct().Count());
        Assert.DoesNotContain(0UL, ids);
    }

    private sealed class ThrowingArguments
    {
        public int Value => throw new InvalidOperationException("Serialization must not run.");
    }
}
