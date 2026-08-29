using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Incant.Base.Log;

internal sealed class LogRuntime
{
    private const int MaxEventsPerDrain = 4096;

    private readonly AutoResetEvent _wakeEvent = new(false);
    private readonly ConcurrentQueue<LogControlRequest> _incomingControlRequests = new();
    private readonly ConcurrentQueue<LogFlushRequest> _incomingFlushRequests = new();
    private readonly TimeSpan _flushInterval;
    private readonly long _id;
    private readonly int _processId;
    private readonly string _processName;
    private readonly Lock _producerLock = new();
    private readonly int _queueCapacity;
    private readonly DateTimeOffset _startTime;
    private readonly long _startTimestamp;
    private readonly ManualResetEventSlim _startupCompleted = new(false);
    private readonly List<LogSinkState> _sinks;
    private readonly Thread _worker;
    private readonly List<LogFlushRequest> _workerFlushRequests = [];
    private Exception? _startupException;
    private LogProducer[] _producers = [];
    private int _isAccepting;
    private int _isStopRequested;
    private int _isWorkerCompleted;
    private int _isWorkerFaulted;
    private int _globalMinimumLevel;
    private int _minimumLevel = (int)LogLevel.None;
    private int _nextProducerId;
    private long _nextOutputSequence;
    private long _lastFlushTimestamp;

    internal LogRuntime(
        long id,
        LogOptions options,
        IReadOnlyList<ILogSink> sinks,
        LogLevel minimumLevel)
    {
        ValidateOptions(options);
        ValidateMinimumLevel(minimumLevel);

        _id = id;
        _queueCapacity = options.QueueCapacityPerThread;
        _flushInterval = options.FlushInterval;
        _processId = Environment.ProcessId;
        _processName = GetProcessName();
        _startTime = DateTimeOffset.UtcNow;
        _startTimestamp = Stopwatch.GetTimestamp();
        _lastFlushTimestamp = _startTimestamp;
        _sinks = sinks
            .Select(sink => new LogSinkState(sink, ValidateSink(sink)))
            .ToList();
        _globalMinimumLevel = (int)minimumLevel;
        RecalculateMinimumLevel();
        _worker = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "Incant Log Worker",
        };

        _worker.Start();
        _startupCompleted.Wait();
        if (_startupException is not null)
        {
            _worker.Join();
            _startupCompleted.Dispose();
            _wakeEvent.Dispose();
            ExceptionDispatchInfo.Capture(_startupException).Throw();
        }

        Volatile.Write(ref _isAccepting, 1);
    }

    internal long Id => _id;

    internal int ProcessId => _processId;

    internal bool IsAccepting => Volatile.Read(ref _isAccepting) != 0;

    internal bool IsWorkerFaulted => Volatile.Read(ref _isWorkerFaulted) != 0;

    internal LogLevel MinimumLevel => (LogLevel)Volatile.Read(ref _minimumLevel);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsEnabled(LogLevel level)
    {
        return IsAccepting && level >= MinimumLevel && level < LogLevel.None;
    }

    internal LogProducer? RegisterProducer()
    {
        lock (_producerLock)
        {
            if (!IsAccepting)
            {
                return null;
            }

            int producerId = ++_nextProducerId;
            var producer = new LogProducer(_id, producerId, _queueCapacity);
            LogProducer[] producers = [.. _producers, producer];
            Volatile.Write(ref _producers, producers);
            return producer;
        }
    }

    internal void WakeWorker()
    {
        _wakeEvent.Set();
    }

    internal void AddSink(ILogSink sink, LogLevel minimumLevel)
    {
        Flush();
        ExecuteControl(runtime => runtime.AddSinkOnWorker(sink, minimumLevel));
    }

    internal void RemoveSink(ILogSink sink)
    {
        Flush();
        ExecuteControl(runtime => runtime.RemoveSinkOnWorker(sink));
    }

    internal void ClearSinks()
    {
        Flush();
        ExecuteControl(static runtime => runtime.ClearSinksOnWorker());
    }

    internal void SetMinimumLevel(LogLevel minimumLevel)
    {
        ExecuteControl(runtime => runtime.SetMinimumLevelOnWorker(minimumLevel));
    }

    internal void Flush()
    {
        ThrowIfWorkerFaulted();
        LogProducer[] producers = Volatile.Read(ref _producers);
        var targets = new LogFlushTarget[producers.Length];
        for (int index = 0; index < producers.Length; ++index)
        {
            LogProducer producer = producers[index];
            targets[index] = new LogFlushTarget(producer, producer.PublishedSequence);
        }

        var request = new LogFlushRequest(targets);
        _incomingFlushRequests.Enqueue(request);
        if (Volatile.Read(ref _isWorkerCompleted) != 0)
        {
            request.Fail(new InvalidOperationException("The log worker is no longer running."));
        }

        WakeWorker();
        request.Wait();
    }

    internal void Stop()
    {
        lock (_producerLock)
        {
            Volatile.Write(ref _isAccepting, 0);
        }

        var spinWait = new SpinWait();
        while (HasActiveWriter())
        {
            spinWait.SpinOnce();
        }

        Volatile.Write(ref _isStopRequested, 1);
        WakeWorker();
        _worker.Join();
        foreach (LogProducer producer in Volatile.Read(ref _producers))
        {
            producer.ReleaseStorage();
        }

        _startupCompleted.Dispose();
        _wakeEvent.Dispose();
    }

    internal DateTimeOffset GetTimestamp(long timestamp)
    {
        long elapsedTicks = Math.Max(0, timestamp - _startTimestamp);
        return _startTime + Stopwatch.GetElapsedTime(0, elapsedTicks);
    }

    internal long GetElapsedNanoseconds(long timestamp)
    {
        long elapsedTicks = Math.Max(0, timestamp - _startTimestamp);
        double nanoseconds = elapsedTicks * (1_000_000_000d / Stopwatch.Frequency);
        return nanoseconds >= long.MaxValue ? long.MaxValue : (long)Math.Round(nanoseconds);
    }

    internal long NextOutputSequence() => ++_nextOutputSequence;

    internal void EmitEmergency(LogLevel level, string messageTemplate)
    {
        if (level >= LogLevel.Warning)
        {
            EmergencyLog.Write($"[{level}] {messageTemplate}");
        }
    }

    internal static void ValidateOptions(LogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        int capacity = options.QueueCapacityPerThread;
        if (capacity < 2 || capacity > 65_536 || !BitOperations.IsPow2((uint)capacity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The per-thread queue capacity must be a power of two from 2 through 65536.");
        }

        if (options.FlushInterval <= TimeSpan.Zero || options.FlushInterval > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The flush interval must be greater than zero and no longer than one day.");
        }
    }

    internal static void ValidateMinimumLevel(LogLevel minimumLevel)
    {
        if (minimumLevel < LogLevel.Trace || minimumLevel > LogLevel.None)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLevel), "The minimum level is invalid.");
        }
    }

    internal static LogLevel ValidateSink(ILogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        LogLevel minimumLevel = sink.MinimumLevel;
        if (minimumLevel < LogLevel.Trace || minimumLevel > LogLevel.None)
        {
            throw new ArgumentOutOfRangeException(nameof(sink), "The sink minimum level is invalid.");
        }

        return minimumLevel;
    }

    private static string GetProcessName()
    {
        string? name = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        return string.IsNullOrWhiteSpace(name) ? AppDomain.CurrentDomain.FriendlyName : name;
    }

    private bool HasActiveWriter()
    {
        foreach (LogProducer producer in Volatile.Read(ref _producers))
        {
            if (producer.IsWriting)
            {
                return true;
            }
        }

        return false;
    }

    private void WorkerMain()
    {
        try
        {
            StartSinks();
            _startupCompleted.Set();
            WorkerLoop();
        }
        catch (Exception exception)
        {
            if (!_startupCompleted.IsSet)
            {
                _startupException = exception;
                _startupCompleted.Set();
            }
            else
            {
                Volatile.Write(ref _isWorkerFaulted, 1);
                EmergencyLog.Write("The log worker failed.", exception);
            }
        }
        finally
        {
            Volatile.Write(ref _isWorkerCompleted, 1);
            FailPendingControlRequests();
            FailPendingFlushRequests();
            FlushAndDisposeSinks();
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            DrainIncomingControlRequests();
            DrainIncomingFlushRequests();
            bool processedEvents = DrainEvents();
            EmitDroppedEventSummaries();
            CompleteSatisfiedFlushRequests();
            FlushPeriodically();

            if (Volatile.Read(ref _isStopRequested) != 0 && !HasQueuedEvents())
            {
                CompleteSatisfiedFlushRequests();
                FlushSinks();
                return;
            }

            if (!processedEvents)
            {
                int waitMilliseconds = Math.Max(1, (int)Math.Min(_flushInterval.TotalMilliseconds, int.MaxValue));
                _wakeEvent.WaitOne(waitMilliseconds);
            }
        }
    }

    private void StartSinks()
    {
        var context = new LogSinkContext(_processId, _processName, Stopwatch.Frequency);
        foreach (LogSinkState sink in _sinks)
        {
            sink.Sink.Start(context);
            sink.IsStarted = true;
        }
    }

    private void ExecuteControl(Action<LogRuntime> action)
    {
        ThrowIfWorkerFaulted();
        var request = new LogControlRequest(action);
        _incomingControlRequests.Enqueue(request);
        if (Volatile.Read(ref _isWorkerCompleted) != 0)
        {
            request.Fail(new InvalidOperationException("The log worker is no longer running."));
        }

        WakeWorker();
        request.Wait();
    }

    private void DrainIncomingControlRequests()
    {
        while (_incomingControlRequests.TryDequeue(out LogControlRequest? request))
        {
            request.Execute(this);
        }
    }

    private void AddSinkOnWorker(ILogSink sink, LogLevel minimumLevel)
    {
        var context = new LogSinkContext(_processId, _processName, Stopwatch.Frequency);
        var state = new LogSinkState(sink, minimumLevel);
        sink.Start(context);
        state.IsStarted = true;
        _sinks.Add(state);
        RecalculateMinimumLevel();
    }

    private void RemoveSinkOnWorker(ILogSink sink)
    {
        int index = _sinks.FindIndex(state => ReferenceEquals(state.Sink, sink));
        if (index < 0)
        {
            return;
        }

        LogSinkState state = _sinks[index];
        TryDisposeSink(state);
        _sinks.RemoveAt(index);
        RecalculateMinimumLevel();
    }

    private void ClearSinksOnWorker()
    {
        foreach (LogSinkState sink in _sinks)
        {
            TryDisposeSink(sink);
        }

        _sinks.Clear();
        RecalculateMinimumLevel();
    }

    private void SetMinimumLevelOnWorker(LogLevel minimumLevel)
    {
        _globalMinimumLevel = (int)minimumLevel;
        RecalculateMinimumLevel();
    }

    private void RecalculateMinimumLevel()
    {
        LogLevel sinkMinimumLevel = LogLevel.None;
        foreach (LogSinkState sink in _sinks)
        {
            if (sink.IsActive)
            {
                sinkMinimumLevel = (LogLevel)Math.Min((int)sinkMinimumLevel, (int)sink.MinimumLevel);
            }
        }

        int effectiveMinimumLevel = Math.Max(_globalMinimumLevel, (int)sinkMinimumLevel);
        Volatile.Write(ref _minimumLevel, effectiveMinimumLevel);
    }

    private bool DrainEvents()
    {
        var queue = new PriorityQueue<LogProducer, LogSortKey>();
        foreach (LogProducer producer in Volatile.Read(ref _producers))
        {
            if (producer.TryPeekSortKey(out LogSortKey sortKey))
            {
                queue.Enqueue(producer, sortKey);
            }
        }

        int processed = 0;
        while (processed < MaxEventsPerDrain && queue.TryDequeue(out LogProducer? producer, out _))
        {
            ProcessRecord(producer);
            ++processed;

            if (producer.TryPeekSortKey(out LogSortKey nextSortKey))
            {
                queue.Enqueue(producer, nextSortKey);
            }
        }

        return processed > 0;
    }

    private void ProcessRecord(LogProducer producer)
    {
        ref LogRecord record = ref producer.PeekRecord();
        try
        {
            RenderedLogEvent logEvent = LogMessageRenderer.Render(this, producer, ref record);
            Dispatch(logEvent);
            if (logEvent.Level >= LogLevel.Error)
            {
                FlushSinks();
            }
        }
        catch (Exception exception)
        {
            EmergencyLog.Write("A log event could not be rendered.", exception);
        }
        finally
        {
            record.Release();
            producer.ReleaseRecord();
        }
    }

    private void Dispatch(RenderedLogEvent logEvent)
    {
        foreach (LogSinkState sink in _sinks)
        {
            if (!sink.IsActive || logEvent.Level < sink.MinimumLevel)
            {
                continue;
            }

            try
            {
                sink.Sink.Emit(logEvent);
            }
            catch (Exception exception)
            {
                sink.IsActive = false;
                EmergencyLog.Write($"Log sink '{sink.Sink.GetType().FullName}' failed.", exception);
                TryDisposeSink(sink);
                RecalculateMinimumLevel();
            }
        }
    }

    private void EmitDroppedEventSummaries()
    {
        foreach (LogProducer producer in Volatile.Read(ref _producers))
        {
            (long traceCount, long debugCount) = producer.TakeDroppedCounts();
            long totalCount = traceCount + debugCount;
            if (totalCount == 0)
            {
                continue;
            }

            long timestamp = Stopwatch.GetTimestamp();
            string message = $"Dropped {totalCount} low-priority log events from thread {producer.ThreadId}.";
            var root = new TextScope([new LiteralText(message)]);
            var logEvent = new RenderedLogEvent(
                GetTimestamp(timestamp),
                GetElapsedNanoseconds(timestamp),
                NextOutputSequence(),
                LogLevel.Warning,
                LogCategory.Logging,
                _processId,
                producer.ThreadId,
                producer.ThreadName,
                message,
                message,
                root,
                [],
                null,
                null);
            Dispatch(logEvent);
        }
    }

    private void DrainIncomingFlushRequests()
    {
        while (_incomingFlushRequests.TryDequeue(out LogFlushRequest? request))
        {
            _workerFlushRequests.Add(request);
        }
    }

    private void CompleteSatisfiedFlushRequests()
    {
        for (int index = _workerFlushRequests.Count - 1; index >= 0; --index)
        {
            LogFlushRequest request = _workerFlushRequests[index];
            if (!request.IsSatisfied)
            {
                continue;
            }

            FlushSinks();
            request.Complete();
            _workerFlushRequests.RemoveAt(index);
        }
    }

    private void FlushPeriodically()
    {
        long timestamp = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastFlushTimestamp, timestamp) < _flushInterval)
        {
            return;
        }

        FlushSinks();
        _lastFlushTimestamp = timestamp;
    }

    private void FlushSinks()
    {
        foreach (LogSinkState sink in _sinks)
        {
            if (!sink.IsActive)
            {
                continue;
            }

            try
            {
                sink.Sink.Flush();
            }
            catch (Exception exception)
            {
                sink.IsActive = false;
                EmergencyLog.Write(
                    $"Log sink '{sink.Sink.GetType().FullName}' failed while flushing.",
                    exception);
                TryDisposeSink(sink);
                RecalculateMinimumLevel();
            }
        }
    }

    private void FlushAndDisposeSinks()
    {
        foreach (LogSinkState sink in _sinks)
        {
            if (sink.IsActive && sink.IsStarted)
            {
                try
                {
                    sink.Sink.Flush();
                }
                catch (Exception exception)
                {
                    EmergencyLog.Write(
                        $"Log sink '{sink.Sink.GetType().FullName}' failed while flushing.",
                        exception);
                }
            }

            TryDisposeSink(sink);
        }
    }

    private static void TryDisposeSink(LogSinkState sink)
    {
        if (sink.IsDisposed)
        {
            return;
        }

        try
        {
            sink.Sink.Dispose();
        }
        catch (Exception exception)
        {
            EmergencyLog.Write(
                $"Log sink '{sink.Sink.GetType().FullName}' failed while disposing.",
                exception);
        }
        finally
        {
            sink.IsDisposed = true;
            sink.IsActive = false;
        }
    }

    private bool HasQueuedEvents()
    {
        foreach (LogProducer producer in Volatile.Read(ref _producers))
        {
            if (producer.HasRecords)
            {
                return true;
            }
        }

        return false;
    }

    private void ThrowIfWorkerFaulted()
    {
        if (IsWorkerFaulted)
        {
            throw new InvalidOperationException("The log worker is no longer running.");
        }
    }

    private void FailPendingFlushRequests()
    {
        var exception = new InvalidOperationException("The log worker stopped before the flush completed.");
        while (_incomingFlushRequests.TryDequeue(out LogFlushRequest? request))
        {
            request.Fail(exception);
        }

        foreach (LogFlushRequest request in _workerFlushRequests)
        {
            request.Fail(exception);
        }

        _workerFlushRequests.Clear();
    }

    private void FailPendingControlRequests()
    {
        var exception = new InvalidOperationException("The log worker stopped before the operation completed.");
        while (_incomingControlRequests.TryDequeue(out LogControlRequest? request))
        {
            request.Fail(exception);
        }
    }
}

internal sealed class LogProducer
{
    private int _mask;
    private LogRecord[] _records;
    private long _droppedDebug;
    private long _droppedTrace;
    private long _publishedSequence;
    private long _readSequence;
    private long _writeSequence;
    private int _isWriting;

    internal LogProducer(long runtimeId, int id, int capacity)
    {
        RuntimeId = runtimeId;
        Id = id;
        ThreadId = Environment.CurrentManagedThreadId;
        ThreadName = Thread.CurrentThread.Name ?? $"Thread {ThreadId}";
        _records = new LogRecord[capacity];
        _mask = capacity - 1;
    }

    internal long RuntimeId { get; }

    internal int Id { get; }

    internal int ThreadId { get; }

    internal string ThreadName { get; }

    internal bool IsWriting => Volatile.Read(ref _isWriting) != 0;

    internal bool HasRecords => ReadSequence < PublishedSequence;

    internal long PublishedSequence => Volatile.Read(ref _publishedSequence);

    internal long ReadSequence => Volatile.Read(ref _readSequence);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryBeginWrite(LogRuntime runtime)
    {
        Volatile.Write(ref _isWriting, 1);
        if (runtime.IsAccepting)
        {
            return true;
        }

        Volatile.Write(ref _isWriting, 0);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EndWrite()
    {
        Volatile.Write(ref _isWriting, 0);
    }

    internal bool TryReserve(LogRuntime runtime, LogLevel level, out int index, out long localSequence)
    {
        var spinWait = new SpinWait();
        while (_writeSequence - Volatile.Read(ref _readSequence) == _records.Length)
        {
            if (level <= LogLevel.Debug)
            {
                if (level == LogLevel.Trace)
                {
                    Interlocked.Increment(ref _droppedTrace);
                }
                else
                {
                    Interlocked.Increment(ref _droppedDebug);
                }

                index = 0;
                localSequence = 0;
                return false;
            }

            if (!runtime.IsAccepting || runtime.IsWorkerFaulted)
            {
                index = 0;
                localSequence = 0;
                return false;
            }

            spinWait.SpinOnce();
        }

        localSequence = _writeSequence;
        index = (int)(_writeSequence & _mask);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref LogRecord GetRecord(int index) => ref _records[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Publish(LogRuntime runtime)
    {
        long previousWriteSequence = _writeSequence;
        _writeSequence = previousWriteSequence + 1;
        Volatile.Write(ref _publishedSequence, _writeSequence);
        if (Volatile.Read(ref _readSequence) == previousWriteSequence)
        {
            runtime.WakeWorker();
        }
    }

    internal bool TryPeekSortKey(out LogSortKey sortKey)
    {
        long readSequence = _readSequence;
        if (readSequence >= Volatile.Read(ref _publishedSequence))
        {
            sortKey = default;
            return false;
        }

        ref LogRecord record = ref _records[(int)(readSequence & _mask)];
        sortKey = new LogSortKey(record.Timestamp, Id, record.LocalSequence);
        return true;
    }

    internal ref LogRecord PeekRecord()
    {
        return ref _records[(int)(_readSequence & _mask)];
    }

    internal void ReleaseRecord()
    {
        Volatile.Write(ref _readSequence, _readSequence + 1);
    }

    internal (long TraceCount, long DebugCount) TakeDroppedCounts()
    {
        return (
            Interlocked.Exchange(ref _droppedTrace, 0),
            Interlocked.Exchange(ref _droppedDebug, 0));
    }

    internal void ReleaseStorage()
    {
        _records = [];
        _mask = 0;
    }
}

internal struct LogRecord
{
    private InlineLogArguments _arguments;
    private LogArgument[]? _extraArguments;

    internal LogLevel Level { get; private set; }

    internal LogCategory Category { get; private set; }

    internal string? MessageTemplate { get; private set; }

    internal string? ExceptionText { get; private set; }

    internal long Timestamp { get; private set; }

    internal long LocalSequence { get; private set; }

    internal int ArgumentCount { get; private set; }

    internal TextDecorator? RootDecorator { get; private set; }

    internal void Initialize(
        LogLevel level,
        LogCategory category,
        string? messageTemplate,
        string? exceptionText,
        long timestamp,
        long localSequence,
        int argumentCount,
        TextDecorator? rootDecorator)
    {
        Level = level;
        Category = category;
        MessageTemplate = messageTemplate;
        ExceptionText = exceptionText;
        Timestamp = timestamp;
        LocalSequence = localSequence;
        ArgumentCount = argumentCount;
        RootDecorator = rootDecorator;
        if (argumentCount > 4)
        {
            _extraArguments = ArrayPool<LogArgument>.Shared.Rent(argumentCount - 4);
        }
    }

    internal readonly LogArgument GetArgument(int index)
    {
        if ((uint)index < 4)
        {
            return _arguments[index];
        }

        return _extraArguments![index - 4];
    }

    internal void SetArgument(int index, LogArgument argument)
    {
        if ((uint)index < 4)
        {
            _arguments[index] = argument;
            return;
        }

        _extraArguments![index - 4] = argument;
    }

    internal void Release()
    {
        for (int index = 0; index < ArgumentCount; ++index)
        {
            SetArgument(index, default);
        }

        if (_extraArguments is not null)
        {
            ArrayPool<LogArgument>.Shared.Return(_extraArguments, true);
        }

        this = default;
    }
}

[System.Runtime.CompilerServices.InlineArray(4)]
internal struct InlineLogArguments
{
    private LogArgument _element0;
}

internal readonly struct LogSortKey : IComparable<LogSortKey>
{
    internal LogSortKey(long timestamp, int producerId, long localSequence)
    {
        Timestamp = timestamp;
        ProducerId = producerId;
        LocalSequence = localSequence;
    }

    private long Timestamp { get; }

    private int ProducerId { get; }

    private long LocalSequence { get; }

    public int CompareTo(LogSortKey other)
    {
        int timestampComparison = Timestamp.CompareTo(other.Timestamp);
        if (timestampComparison != 0)
        {
            return timestampComparison;
        }

        int producerComparison = ProducerId.CompareTo(other.ProducerId);
        return producerComparison != 0 ? producerComparison : LocalSequence.CompareTo(other.LocalSequence);
    }
}

internal sealed class LogSinkState
{
    internal LogSinkState(ILogSink sink, LogLevel minimumLevel)
    {
        Sink = sink;
        MinimumLevel = minimumLevel;
    }

    internal ILogSink Sink { get; }

    internal LogLevel MinimumLevel { get; }

    internal bool IsStarted { get; set; }

    internal bool IsDisposed { get; set; }

    internal bool IsActive { get; set; } = true;
}

internal sealed class LogControlRequest
{
    private readonly Action<LogRuntime> _action;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal LogControlRequest(Action<LogRuntime> action)
    {
        _action = action;
    }

    internal void Execute(LogRuntime runtime)
    {
        try
        {
            _action(runtime);
            _completion.TrySetResult();
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
    }

    internal void Fail(Exception exception)
    {
        _completion.TrySetException(exception);
    }

    internal void Wait()
    {
        _completion.Task.GetAwaiter().GetResult();
    }
}

internal readonly record struct LogFlushTarget(LogProducer Producer, long Sequence);

internal sealed class LogFlushRequest
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal LogFlushRequest(LogFlushTarget[] targets)
    {
        Targets = targets;
    }

    internal LogFlushTarget[] Targets { get; }

    internal bool IsSatisfied
    {
        get
        {
            foreach (LogFlushTarget target in Targets)
            {
                if (target.Producer.ReadSequence < target.Sequence)
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal void Complete() => _completion.TrySetResult();

    internal void Fail(Exception exception)
    {
        _completion.TrySetException(exception);
    }

    internal void Wait()
    {
        _completion.Task.GetAwaiter().GetResult();
    }
}

internal static class EmergencyLog
{
    private static readonly Lock s_lock = new();

    internal static void Write(string message)
    {
        lock (s_lock)
        {
            try
            {
                Console.Error.WriteLine($"[Incant Log Failure] {message}");
            }
            catch
            {
                // There is no remaining safe output path.
            }
        }
    }

    internal static void Write(string message, Exception exception)
    {
        string exceptionText;
        try
        {
            exceptionText = exception.ToString();
        }
        catch
        {
            exceptionText = $"Exception details are unavailable ({exception.GetType().FullName}).";
        }

        Write($"{message} {exceptionText}");
    }
}
