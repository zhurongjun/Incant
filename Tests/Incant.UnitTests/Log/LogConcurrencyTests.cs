using Incant.Base.Log;
using LogRecorder = Incant.Base.Log.Log;

namespace Incant.UnitTests.Log;

[Collection(LogCollection.Name)]
public sealed class LogConcurrencyTests : LogTestBase
{
    [Fact]
    public void MultipleProducerThreadsPreserveTheirOwnOrderAndMetadata()
    {
        const int ProducerCount = 4;
        const int EventsPerProducer = 40;
        var sink = new CollectingLogSink();
        Start(sink, 256);
        using var startGate = new ManualResetEventSlim(false);
        Thread[] threads = Enumerable.Range(0, ProducerCount)
            .Select(
                producerIndex =>
                {
                    var thread = new Thread(
                        () =>
                        {
                            startGate.Wait();
                            for (int eventIndex = 0; eventIndex < EventsPerProducer; ++eventIndex)
                            {
                                LogRecorder.Info(
                                    "Producer {Producer} event {Index}",
                                    producerIndex,
                                    eventIndex);
                            }
                        })
                    {
                        Name = $"Log Producer {producerIndex}",
                    };
                    return thread;
                })
            .ToArray();

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        startGate.Set();
        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        LogRecorder.Stop();

        Assert.Equal(ProducerCount * EventsPerProducer, sink.Events.Count);
        Assert.Equal(
            Enumerable.Range(1, ProducerCount * EventsPerProducer).Select(value => (long)value),
            sink.Events.Select(logEvent => logEvent.Sequence));
        foreach (IGrouping<long, RenderedLogEvent> producerEvents in sink.Events.GroupBy(
                     logEvent => (long)logEvent.Properties[0].Value.Value!))
        {
            Assert.Equal(
                Enumerable.Range(0, EventsPerProducer).Select(value => (long)value),
                producerEvents.Select(logEvent => (long)logEvent.Properties[1].Value.Value!));
            Assert.All(
                producerEvents,
                logEvent => Assert.Equal($"Log Producer {producerEvents.Key}", logEvent.ThreadName));
        }
    }

    [Fact]
    public void FlushIsABarrierForEventsAlreadyPublished()
    {
        var sink = new CollectingLogSink();
        Start(sink, 16);

        LogRecorder.Info("one");
        LogRecorder.Info("two");
        LogRecorder.Flush();

        Assert.Equal(["one", "two"], sink.Events.Select(logEvent => logEvent.Message));
        LogRecorder.Info("three");
        LogRecorder.Stop();
        Assert.Equal(["one", "two", "three"], sink.Events.Select(logEvent => logEvent.Message));
    }

    [Fact]
    public void FullQueueDropsLowLevelsButWaitsForReliableLevelsAndReportsLoss()
    {
        using var beforeReliable = new ManualResetEventSlim(false);
        using var reliableReturned = new ManualResetEventSlim(false);
        var sink = new BlockingSink();
        Start(sink, 2);
        var producer = new Thread(
            () =>
            {
                LogRecorder.Info("blocking");
                if (!sink.Entered.Wait(TimeSpan.FromSeconds(5)))
                {
                    return;
                }

                for (int index = 0; index < 32; ++index)
                {
                    LogRecorder.Debug("debug {Index}", index);
                }

                beforeReliable.Set();
                LogRecorder.Info("reliable");
                reliableReturned.Set();
            });

        producer.Start();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        bool entered = sink.Entered.Wait(TimeSpan.FromSeconds(5), cancellationToken);
        bool reachedReliable = beforeReliable.Wait(TimeSpan.FromSeconds(5), cancellationToken);
        bool returnedWhileBlocked = reliableReturned.Wait(TimeSpan.FromMilliseconds(100), cancellationToken);
        sink.Release();
        bool returnedAfterRelease = reliableReturned.Wait(TimeSpan.FromSeconds(5), cancellationToken);
        bool producerStopped = producer.Join(TimeSpan.FromSeconds(5));
        LogRecorder.Stop();

        Assert.True(entered);
        Assert.True(reachedReliable);
        Assert.False(returnedWhileBlocked);
        Assert.True(returnedAfterRelease);
        Assert.True(producerStopped);
        Assert.Contains(sink.Events, logEvent => logEvent.Message == "reliable");
        Assert.Contains(
            sink.Events,
            logEvent => logEvent.Level == LogLevel.Warning
                && logEvent.Category == LogCategory.Logging
                && logEvent.Message.StartsWith("Dropped ", StringComparison.Ordinal));
        Assert.True(sink.Events.Count(logEvent => logEvent.Level == LogLevel.Debug) < 32);
    }

    [Fact]
    public void ConcurrentStopDrainsAcceptedEventsAndRejectsLaterWrites()
    {
        const int ProducerCount = 4;
        var sink = new CollectingLogSink();
        Start(sink, 1024);
        using var cancellation = new CancellationTokenSource();
        using var startGate = new ManualResetEventSlim(false);
        using var ready = new CountdownEvent(ProducerCount);
        Thread[] threads = Enumerable.Range(0, ProducerCount)
            .Select(
                producerIndex => new Thread(
                    () =>
                    {
                        ready.Signal();
                        startGate.Wait();
                        int index = 0;
                        while (!cancellation.IsCancellationRequested)
                        {
                            LogRecorder.Info("Producer {Producer} event {Index}", producerIndex, index++);
                        }
                    }))
            .ToArray();
        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        bool producersReady = ready.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        startGate.Set();
        bool eventObserved = SpinWait.SpinUntil(() => sink.Events.Count != 0, TimeSpan.FromSeconds(5));
        if (LogRecorder.IsRunning)
        {
            LogRecorder.Stop();
        }

        cancellation.Cancel();
        var stopped = new List<bool>(threads.Length);
        foreach (Thread thread in threads)
        {
            stopped.Add(thread.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.True(producersReady);
        Assert.True(eventObserved);
        Assert.All(stopped, Assert.True);
        Assert.False(LogRecorder.IsRunning);
        Assert.NotEmpty(sink.Events);
        Assert.Equal(
            Enumerable.Range(1, sink.Events.Count).Select(value => (long)value),
            sink.Events.Select(logEvent => logEvent.Sequence));
    }

    private static void Start(ILogSink sink, int capacity)
    {
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(sink);
        LogRecorder.Start(
            new LogOptions
            {
                QueueCapacityPerThread = capacity,
                FlushInterval = TimeSpan.FromSeconds(30),
            });
    }

    private sealed class BlockingSink : ILogSink
    {
        private readonly Lock _eventLock = new();
        private readonly List<RenderedLogEvent> _events = [];
        private readonly ManualResetEventSlim _release = new(false);
        private int _emitCount;

        public LogLevel MinimumLevel => LogLevel.Trace;

        internal ManualResetEventSlim Entered { get; } = new(false);

        internal IReadOnlyList<RenderedLogEvent> Events
        {
            get
            {
                lock (_eventLock)
                {
                    return _events.ToArray();
                }
            }
        }

        public void Start(LogSinkContext context)
        {
        }

        public void Emit(RenderedLogEvent logEvent)
        {
            if (Interlocked.Increment(ref _emitCount) == 1)
            {
                Entered.Set();
                _release.Wait();
            }

            lock (_eventLock)
            {
                _events.Add(logEvent);
            }
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
            Entered.Dispose();
        }

        internal void Release()
        {
            _release.Set();
        }
    }
}
