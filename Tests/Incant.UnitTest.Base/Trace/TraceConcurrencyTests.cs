using Incant.Base.Trace;
using TraceRecorder = Incant.Base.Trace.Trace;

namespace Incant.UnitTest.Base.Trace;

[Collection(TraceCollection.Name)]
public sealed class TraceConcurrencyTests : TraceTestBase
{
    [Fact]
    public void NamedThreadsPreserveMetadataAndPerThreadEventOrder()
    {
        const int EventCountPerThread = 3;
        using var startGate = new ManualResetEventSlim();
        var threadIds = new int[2];
        Thread firstThread = CreateEventThread(
            "Trace worker A",
            "a",
            0,
            startGate,
            threadIds,
            EventCountPerThread);
        Thread secondThread = CreateEventThread(
            "Trace worker B",
            "b",
            1,
            startGate,
            threadIds,
            EventCountPerThread);

        TraceRecorder.Start();
        firstThread.Start();
        secondThread.Start();
        startGate.Set();
        firstThread.Join();
        secondThread.Join();
        TraceCapture capture = TraceRecorder.Stop();

        TraceEvent[] events = capture.Events.ToArray();
        Assert.Equal(EventCountPerThread * 2, events.Length);
        Assert.Equal(2, events.Select(traceEvent => traceEvent.ThreadId).Distinct().Count());
        AssertSortedByTimestamp(events);
        Assert.Equal(
            ["a-0", "a-1", "a-2"],
            events.Where(traceEvent => traceEvent.ThreadId == threadIds[0]).Select(traceEvent => traceEvent.Name));
        Assert.Equal(
            ["b-0", "b-1", "b-2"],
            events.Where(traceEvent => traceEvent.ThreadId == threadIds[1]).Select(traceEvent => traceEvent.Name));

        TraceThreadInfo[] threads = capture.Threads.ToArray();
        Assert.Equal(2, threads.Length);
        Assert.Contains(threads, thread => thread.Id == threadIds[0] && thread.Name == "Trace worker A");
        Assert.Contains(threads, thread => thread.Id == threadIds[1] && thread.Name == "Trace worker B");
    }

    [Fact]
    public void AsyncAndFlowEventsCorrelateAcrossThreads()
    {
        const ulong CorrelationId = 0xfedcba9876543210UL;
        int mainThreadId = Environment.CurrentManagedThreadId;
        int workerThreadId = 0;
        var worker = new Thread(
            () =>
            {
                workerThreadId = Environment.CurrentManagedThreadId;
                TraceRecorder.AsyncEvent(TraceCategory.Scheduler, "async event", CorrelationId);
                TraceRecorder.FlowStep(TraceCategory.Dependency, "flow step", CorrelationId);
            })
        {
            Name = "Correlation worker",
        };

        TraceRecorder.Start();
        TraceRecorder.AsyncBegin(TraceCategory.Scheduler, "async begin", CorrelationId);
        TraceRecorder.FlowStart(TraceCategory.Dependency, "flow start", CorrelationId);
        worker.Start();
        worker.Join();
        TraceRecorder.AsyncEnd(TraceCategory.Scheduler, "async end", CorrelationId);
        TraceRecorder.FlowEnd(TraceCategory.Dependency, "flow end", CorrelationId);
        TraceCapture capture = TraceRecorder.Stop();

        AssertEvent(capture, "async begin", TraceEventKind.AsyncBegin, CorrelationId, mainThreadId);
        AssertEvent(capture, "async event", TraceEventKind.AsyncEvent, CorrelationId, workerThreadId);
        AssertEvent(capture, "async end", TraceEventKind.AsyncEnd, CorrelationId, mainThreadId);
        AssertEvent(capture, "flow start", TraceEventKind.FlowStart, CorrelationId, mainThreadId);
        AssertEvent(capture, "flow step", TraceEventKind.FlowStep, CorrelationId, workerThreadId);
        AssertEvent(capture, "flow end", TraceEventKind.FlowEnd, CorrelationId, mainThreadId);
        Assert.Contains(
            capture.Threads.ToArray(),
            thread => thread.Id == workerThreadId && thread.Name == "Correlation worker");
    }

    private static Thread CreateEventThread(
        string threadName,
        string eventPrefix,
        int threadIndex,
        ManualResetEventSlim startGate,
        int[] threadIds,
        int eventCount)
    {
        return new Thread(
            () =>
            {
                startGate.Wait();
                threadIds[threadIndex] = Environment.CurrentManagedThreadId;
                for (int index = 0; index < eventCount; ++index)
                {
                    TraceRecorder.Event(TraceCategory.General, $"{eventPrefix}-{index}");
                }
            })
        {
            Name = threadName,
        };
    }

    private static void AssertSortedByTimestamp(TraceEvent[] events)
    {
        for (int index = 1; index < events.Length; ++index)
        {
            Assert.True(events[index - 1].TimestampTicks <= events[index].TimestampTicks);
        }
    }

    private static void AssertEvent(
        TraceCapture capture,
        string name,
        TraceEventKind expectedKind,
        ulong expectedId,
        int expectedThreadId)
    {
        TraceEvent traceEvent = capture.Events.ToArray().Single(item => item.Name == name);
        Assert.Equal(expectedKind, traceEvent.Kind);
        Assert.Equal(expectedId, traceEvent.Id);
        Assert.Equal(expectedThreadId, traceEvent.ThreadId);
    }
}
