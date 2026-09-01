using BenchmarkDotNet.Attributes;
using Incant.Base.Log;
using LogRecorder = Incant.Base.Log.Log;

namespace Incant.Bench.Log;

[MemoryDiagnoser]
[LogBenchmarkJob(1)]
public class LogFlushBenchmarks
{
    private const int EventsPerBatch = 64;

    [GlobalSetup]
    public void Setup()
    {
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(new BenchmarkNullLogSink());
        LogRecorder.Start(
            new LogOptions
            {
                QueueCapacityPerThread = 256,
                FlushInterval = TimeSpan.FromSeconds(30),
            });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (LogRecorder.IsRunning)
        {
            LogRecorder.Stop();
        }
    }

    [Benchmark(OperationsPerInvoke = EventsPerBatch)]
    public void RecordAndFlushBatch()
    {
        for (int index = 0; index < EventsPerBatch; ++index)
        {
            LogRecorder.Info("event {Index}", index);
        }

        LogRecorder.Flush();
    }
}

[MemoryDiagnoser]
[LogBenchmarkJob(1)]
public class QueueRolloverLogBenchmarks
{
    private const int EventsPerBatch = 1024;

    [GlobalSetup]
    public void Setup()
    {
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(new BenchmarkNullLogSink());
        LogRecorder.Start(
            new LogOptions
            {
                QueueCapacityPerThread = 256,
                FlushInterval = TimeSpan.FromSeconds(30),
            });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (LogRecorder.IsRunning)
        {
            LogRecorder.Stop();
        }
    }

    [Benchmark(OperationsPerInvoke = EventsPerBatch)]
    public void RecordAcrossQueueWraps()
    {
        for (int index = 0; index < EventsPerBatch; ++index)
        {
            LogRecorder.Info("event {Index}", index);
        }

        LogRecorder.Flush();
    }
}

[MemoryDiagnoser]
[LogBenchmarkJob(1)]
public class LowPriorityDropLogBenchmarks
{
    private const int EventsPerBatch = 256;
    private BlockingBenchmarkSink? _sink;

    [IterationSetup]
    public void Setup()
    {
        _sink = new BlockingBenchmarkSink();
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(_sink);
        LogRecorder.Start(
            new LogOptions
            {
                QueueCapacityPerThread = 2,
                FlushInterval = TimeSpan.FromSeconds(30),
            });
        LogRecorder.Info("block worker");
        _sink.WaitUntilBlocked();
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _sink?.Release();
        if (LogRecorder.IsRunning)
        {
            LogRecorder.Stop();
        }
    }

    [Benchmark(OperationsPerInvoke = EventsPerBatch)]
    public void DropBatch()
    {
        for (int index = 0; index < EventsPerBatch; ++index)
        {
            LogRecorder.Debug("event {Index}", index);
        }
    }

    private sealed class BlockingBenchmarkSink : ILogSink
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private int _emitCount;

        public LogLevel MinimumLevel => LogLevel.Trace;

        public void Start(LogSinkContext context)
        {
        }

        public void Emit(RenderedLogEvent logEvent)
        {
            if (Interlocked.Increment(ref _emitCount) != 1)
            {
                return;
            }

            _entered.Set();
            _release.Wait();
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }

        internal void WaitUntilBlocked()
        {
            _entered.Wait();
        }

        internal void Release()
        {
            _release.Set();
        }
    }
}

[MemoryDiagnoser]
[LogBenchmarkJob(1)]
public class StopLogBenchmarks
{
    [IterationSetup]
    public void Setup()
    {
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(new BenchmarkNullLogSink());
        LogRecorder.Start(new LogOptions());
    }

    [IterationCleanup]
    public void Cleanup()
    {
        if (LogRecorder.IsRunning)
        {
            LogRecorder.Stop();
        }
    }

    [Benchmark]
    public void StopAfterOneEvent()
    {
        LogRecorder.Info("event");
        LogRecorder.Stop();
    }
}

[MemoryDiagnoser]
[LogBenchmarkJob(1)]
public class MultithreadLogBenchmarks
{
    private const int EventsPerWorker = 256;
    private const int WorkerCount = 4;

    [GlobalSetup]
    public void Setup()
    {
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(new BenchmarkNullLogSink());
        LogRecorder.Start(
            new LogOptions
            {
                QueueCapacityPerThread = 1024,
                FlushInterval = TimeSpan.FromSeconds(30),
            });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (LogRecorder.IsRunning)
        {
            LogRecorder.Stop();
        }
    }

    [Benchmark(OperationsPerInvoke = WorkerCount * EventsPerWorker)]
    public void RecordBatch()
    {
        Parallel.For(
            0,
            WorkerCount,
            _ =>
            {
                for (int index = 0; index < EventsPerWorker; ++index)
                {
                    LogRecorder.Info("event {Index}", index);
                }
            });
        LogRecorder.Flush();
    }
}
