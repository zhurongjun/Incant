using Incant.Base.Trace;
using TraceRecorder = Incant.Base.Trace.Trace;

namespace Incant.UnitTest.Base.Trace;

[Collection(TraceCollection.Name)]
public sealed class TraceScopeTests : TraceTestBase
{
    [Fact]
    public void NestedScopesContainTheirNestedEvents()
    {
        TraceRecorder.Start();

        using (TraceRecorder.Scope(TraceCategory.Build, "outer"))
        {
            TraceRecorder.Event(TraceCategory.Build, "inside");
            using (TraceRecorder.Scope(TraceCategory.Build, "inner"))
            {
            }
        }

        TraceCapture capture = TraceRecorder.Stop();
        TraceEvent outer = FindEvent(capture, "outer");
        TraceEvent inside = FindEvent(capture, "inside");
        TraceEvent inner = FindEvent(capture, "inner");

        Assert.Equal(TraceEventKind.Complete, outer.Kind);
        Assert.Equal(TraceEventKind.Instant, inside.Kind);
        Assert.Equal(TraceEventKind.Complete, inner.Kind);
        Assert.True(outer.DurationTicks >= 0);
        Assert.True(inner.DurationTicks >= 0);
        Assert.InRange(inside.TimestampTicks, outer.TimestampTicks, outer.TimestampTicks + outer.DurationTicks);
        Assert.InRange(inner.TimestampTicks, outer.TimestampTicks, outer.TimestampTicks + outer.DurationTicks);
        Assert.True(
            inner.TimestampTicks + inner.DurationTicks
            <= outer.TimestampTicks + outer.DurationTicks);
    }

    [Fact]
    public void ScopeDisposeIsIdempotent()
    {
        TraceRecorder.Start();
        TraceScope scope = TraceRecorder.Scope(TraceCategory.Build, "scope");

        scope.Dispose();
        scope.Dispose();
        TraceCapture capture = TraceRecorder.Stop();

        TraceEvent traceEvent = Assert.Single(capture.Events.ToArray());
        Assert.Equal("scope", traceEvent.Name);
        Assert.Equal(TraceEventKind.Complete, traceEvent.Kind);
        Assert.True(traceEvent.DurationTicks >= 0);
    }

    [Fact]
    public void UsingCompletesScopeDuringExceptionUnwind()
    {
        TraceRecorder.Start();

        Assert.Throws<InvalidOperationException>(ThrowInsideScope);
        TraceCapture capture = TraceRecorder.Stop();

        TraceEvent traceEvent = Assert.Single(capture.Events.ToArray());
        Assert.Equal("throwing", traceEvent.Name);
        Assert.Equal(TraceEventKind.Complete, traceEvent.Kind);
        Assert.True(traceEvent.DurationTicks >= 0);
    }

    [Fact]
    public void IncompleteScopesAreExcludedWithoutDiscardingCompletedEvents()
    {
        TraceRecorder.Start();
        TraceRecorder.Scope(TraceCategory.Build, "incomplete");
        TraceRecorder.Event(TraceCategory.Build, "complete");

        TraceCapture capture = TraceRecorder.Stop();

        TraceEvent traceEvent = Assert.Single(capture.Events.ToArray());
        Assert.Equal("complete", traceEvent.Name);
        Assert.Equal(TraceEventKind.Instant, traceEvent.Kind);
    }

    private static TraceEvent FindEvent(TraceCapture capture, string name)
    {
        return capture.Events.ToArray().Single(traceEvent => traceEvent.Name == name);
    }

    private static void ThrowInsideScope()
    {
        using TraceScope scope = TraceRecorder.Scope(TraceCategory.Build, "throwing");
        throw new InvalidOperationException("Expected test exception.");
    }
}
