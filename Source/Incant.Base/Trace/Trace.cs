using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Incant.Base.Trace;

/// <summary>Collects process-wide trace events into explicitly controlled capture sessions.</summary>
/// <remarks>
/// Slow methods use Serilog-style message templates. Named properties bind from left to right, while templates
/// containing only numeric properties bind by position. Double braces escape literal braces, <c>@</c> destructures
/// a value, and <c>$</c> captures its string representation. Alignment and format suffixes are accepted but are not
/// rendered. The original template remains the trace event name and captured properties become its JSON arguments.
/// Enabled Slow calls strictly reject malformed templates and mismatched property counts; disabled calls do not parse
/// templates or inspect property values.
/// </remarks>
public static class Trace
{
    private static TraceSession? s_activeSession;
    private static long s_nextId;
    private static long s_nextSessionId;

    [ThreadStatic]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Thread-static fields use the t_ prefix.")]
    private static TraceThreadWriter? t_writer;

    /// <summary>Gets a value indicating whether a trace session is running.</summary>
    public static bool IsRunning => Volatile.Read(ref s_activeSession) is not null;

    /// <summary>Determines whether any bit in <paramref name="category"/> is enabled for the active session.</summary>
    /// <param name="category">The category mask to test.</param>
    /// <returns><see langword="true"/> when a session is active and at least one category bit is enabled.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnabled(TraceCategory category)
    {
        TraceSession? session = Volatile.Read(ref s_activeSession);
        return session is not null && (session.Categories & category) != 0;
    }

    /// <summary>Starts a new, empty trace session.</summary>
    /// <param name="categories">The categories enabled for the session.</param>
    /// <exception cref="InvalidOperationException">A trace session is already running.</exception>
    /// <remarks>Call this method only at a safe point where no thread is writing an event or holding an active scope.</remarks>
    public static void Start(TraceCategory categories = TraceCategory.All)
    {
        long sessionId = Interlocked.Increment(ref s_nextSessionId);
        var session = new TraceSession(sessionId, categories, Stopwatch.GetTimestamp());
        if (Interlocked.CompareExchange(ref s_activeSession, session, null) is not null)
        {
            throw new InvalidOperationException("A trace session is already running.");
        }
    }

    /// <summary>Stops the active session and returns an independent immutable capture.</summary>
    /// <returns>The completed capture.</returns>
    /// <exception cref="InvalidOperationException">No trace session is running.</exception>
    /// <remarks>
    /// Call this method only at a safe point where no thread is writing an event or holding an active scope.
    /// Incomplete scopes violate this contract and are excluded from the returned capture.
    /// </remarks>
    public static TraceCapture Stop()
    {
        TraceSession? session = Interlocked.Exchange(ref s_activeSession, null);
        if (session is null)
        {
            throw new InvalidOperationException("No trace session is running.");
        }

        long endTimestamp = Stopwatch.GetTimestamp();
        return session.CreateCapture(endTimestamp);
    }

    /// <summary>Creates a process-wide correlation identifier.</summary>
    /// <returns>A value that is unique for the lifetime of the process until the counter wraps.</returns>
    public static ulong CreateId()
    {
        return unchecked((ulong)Interlocked.Increment(ref s_nextId));
    }

    /// <summary>Begins a synchronous complete event.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="name">The event name. Its string reference is retained without copying.</param>
    /// <returns>A scope that completes the event when disposed, or a no-op scope when disabled.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TraceScope Scope(TraceCategory category, string name)
    {
        if (!TryGetSession(category, out TraceSession? session))
        {
            return default;
        }

        return BeginScope(session, category, name, null);
    }

    /// <summary>Begins a synchronous complete event with structured properties captured immediately.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="messageTemplate">The Serilog-style message template retained as the event name.</param>
    /// <param name="propertyValues">The values matched to template properties.</param>
    /// <returns>A scope that completes the event when disposed, or a no-op scope when disabled.</returns>
    /// <exception cref="ArgumentNullException">The enabled call has a null message template.</exception>
    /// <exception cref="FormatException">The enabled call has a malformed message template.</exception>
    /// <exception cref="ArgumentException">The enabled call has mismatched template properties and values.</exception>
    /// <remarks>Serialization occurs only when the category is enabled, and serialization errors propagate.</remarks>
    public static TraceScope ScopeSlow(
        TraceCategory category,
        string messageTemplate,
        params object?[]? propertyValues)
    {
        if (!TryGetSession(category, out TraceSession? session))
        {
            return default;
        }

        TracePayload payload = TracePayload.Create(messageTemplate, propertyValues);
        return BeginScope(session, category, messageTemplate, payload);
    }

    /// <summary>Records an instantaneous event.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="name">The event name. Its string reference is retained without copying.</param>
    /// <param name="scope">The visibility of the event.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Event(
        TraceCategory category,
        string name,
        TraceInstantScope scope = TraceInstantScope.Thread)
    {
        if (!TryGetSession(category, out TraceSession? session))
        {
            return;
        }

        WriteEvent(session, TraceEventKind.Instant, category, name, 0, scope, default, null);
    }

    /// <summary>Records an instantaneous event with structured properties captured immediately.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="messageTemplate">The Serilog-style message template retained as the event name.</param>
    /// <param name="propertyValues">The values matched to template properties.</param>
    /// <exception cref="ArgumentNullException">The enabled call has a null message template.</exception>
    /// <exception cref="FormatException">The enabled call has a malformed message template.</exception>
    /// <exception cref="ArgumentException">The enabled call has mismatched template properties and values.</exception>
    /// <remarks>Serialization occurs only when the category is enabled, and serialization errors propagate.</remarks>
    public static void EventSlow(
        TraceCategory category,
        string messageTemplate,
        params object?[]? propertyValues)
    {
        EventSlow(category, messageTemplate, TraceInstantScope.Thread, propertyValues);
    }

    /// <summary>Records an instantaneous event with structured properties captured immediately.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="messageTemplate">The Serilog-style message template retained as the event name.</param>
    /// <param name="scope">The visibility of the event.</param>
    /// <param name="propertyValues">The values matched to template properties.</param>
    /// <exception cref="ArgumentNullException">The enabled call has a null message template.</exception>
    /// <exception cref="FormatException">The enabled call has a malformed message template.</exception>
    /// <exception cref="ArgumentException">The enabled call has mismatched template properties and values.</exception>
    /// <remarks>Serialization occurs only when the category is enabled, and serialization errors propagate.</remarks>
    public static void EventSlow(
        TraceCategory category,
        string messageTemplate,
        TraceInstantScope scope,
        params object?[]? propertyValues)
    {
        if (!TryGetSession(category, out TraceSession? session))
        {
            return;
        }

        TracePayload payload = TracePayload.Create(messageTemplate, propertyValues);
        WriteEvent(session, TraceEventKind.Instant, category, messageTemplate, 0, scope, default, payload);
    }

    /// <summary>Records a signed integer counter sample.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="name">The event name. Its string reference is retained without copying.</param>
    /// <param name="value">The signed counter value.</param>
    public static void Counter(TraceCategory category, string name, long value)
    {
        if (!TryGetSession(category, out TraceSession? session))
        {
            return;
        }

        WriteEvent(session, TraceEventKind.Counter, category, name, 0, default, new TraceCounterValue(value), null);
    }

    /// <summary>Records an unsigned integer counter sample.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="name">The event name. Its string reference is retained without copying.</param>
    /// <param name="value">The unsigned counter value.</param>
    public static void Counter(TraceCategory category, string name, ulong value)
    {
        if (!TryGetSession(category, out TraceSession? session))
        {
            return;
        }

        WriteEvent(session, TraceEventKind.Counter, category, name, 0, default, new TraceCounterValue(value), null);
    }

    /// <summary>Records a floating-point counter sample.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="name">The event name. Its string reference is retained without copying.</param>
    /// <param name="value">The floating-point counter value.</param>
    public static void Counter(TraceCategory category, string name, double value)
    {
        if (!TryGetSession(category, out TraceSession? session))
        {
            return;
        }

        WriteEvent(session, TraceEventKind.Counter, category, name, 0, default, new TraceCounterValue(value), null);
    }

    /// <summary>Records a counter event with structured properties captured immediately.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="messageTemplate">The Serilog-style message template retained as the event name.</param>
    /// <param name="propertyValues">The values matched to template properties.</param>
    /// <exception cref="ArgumentNullException">The enabled call has a null message template.</exception>
    /// <exception cref="FormatException">The enabled call has a malformed message template.</exception>
    /// <exception cref="ArgumentException">The enabled call has mismatched template properties and values.</exception>
    /// <remarks>Serialization occurs only when the category is enabled, and serialization errors propagate.</remarks>
    public static void CounterSlow(
        TraceCategory category,
        string messageTemplate,
        params object?[]? propertyValues)
    {
        if (!TryGetSession(category, out TraceSession? session))
        {
            return;
        }

        TracePayload payload = TracePayload.Create(messageTemplate, propertyValues);
        WriteEvent(session, TraceEventKind.Counter, category, messageTemplate, 0, default, default, payload);
    }

    /// <summary>Records the beginning of an asynchronous operation.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="name">The event name. Its string reference is retained without copying.</param>
    /// <param name="id">The correlation identifier.</param>
    public static void AsyncBegin(TraceCategory category, string name, ulong id)
    {
        WriteCorrelatedEvent(TraceEventKind.AsyncBegin, category, name, id, null);
    }

    /// <summary>Records an instantaneous event within an asynchronous operation.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="name">The event name. Its string reference is retained without copying.</param>
    /// <param name="id">The correlation identifier.</param>
    public static void AsyncEvent(TraceCategory category, string name, ulong id)
    {
        WriteCorrelatedEvent(TraceEventKind.AsyncEvent, category, name, id, null);
    }

    /// <summary>Records the end of an asynchronous operation.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="name">The event name. Its string reference is retained without copying.</param>
    /// <param name="id">The correlation identifier.</param>
    public static void AsyncEnd(TraceCategory category, string name, ulong id)
    {
        WriteCorrelatedEvent(TraceEventKind.AsyncEnd, category, name, id, null);
    }

    /// <summary>Records the beginning of an asynchronous operation with structured properties.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="messageTemplate">The Serilog-style message template retained as the event name.</param>
    /// <param name="id">The correlation identifier.</param>
    /// <param name="propertyValues">The values matched to template properties.</param>
    /// <exception cref="ArgumentNullException">The enabled call has a null message template.</exception>
    /// <exception cref="FormatException">The enabled call has a malformed message template.</exception>
    /// <exception cref="ArgumentException">The enabled call has mismatched template properties and values.</exception>
    /// <remarks>Serialization occurs only when the category is enabled, and serialization errors propagate.</remarks>
    public static void AsyncBeginSlow(
        TraceCategory category,
        string messageTemplate,
        ulong id,
        params object?[]? propertyValues)
    {
        WriteCorrelatedEventSlow(TraceEventKind.AsyncBegin, category, messageTemplate, id, propertyValues);
    }

    /// <summary>Records an event within an asynchronous operation with structured properties.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="messageTemplate">The Serilog-style message template retained as the event name.</param>
    /// <param name="id">The correlation identifier.</param>
    /// <param name="propertyValues">The values matched to template properties.</param>
    /// <exception cref="ArgumentNullException">The enabled call has a null message template.</exception>
    /// <exception cref="FormatException">The enabled call has a malformed message template.</exception>
    /// <exception cref="ArgumentException">The enabled call has mismatched template properties and values.</exception>
    /// <remarks>Serialization occurs only when the category is enabled, and serialization errors propagate.</remarks>
    public static void AsyncEventSlow(
        TraceCategory category,
        string messageTemplate,
        ulong id,
        params object?[]? propertyValues)
    {
        WriteCorrelatedEventSlow(TraceEventKind.AsyncEvent, category, messageTemplate, id, propertyValues);
    }

    /// <summary>Records the end of an asynchronous operation with structured properties.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="messageTemplate">The Serilog-style message template retained as the event name.</param>
    /// <param name="id">The correlation identifier.</param>
    /// <param name="propertyValues">The values matched to template properties.</param>
    /// <exception cref="ArgumentNullException">The enabled call has a null message template.</exception>
    /// <exception cref="FormatException">The enabled call has a malformed message template.</exception>
    /// <exception cref="ArgumentException">The enabled call has mismatched template properties and values.</exception>
    /// <remarks>Serialization occurs only when the category is enabled, and serialization errors propagate.</remarks>
    public static void AsyncEndSlow(
        TraceCategory category,
        string messageTemplate,
        ulong id,
        params object?[]? propertyValues)
    {
        WriteCorrelatedEventSlow(TraceEventKind.AsyncEnd, category, messageTemplate, id, propertyValues);
    }

    /// <summary>Records the beginning of a flow.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="name">The event name. Its string reference is retained without copying.</param>
    /// <param name="id">The correlation identifier.</param>
    public static void FlowStart(TraceCategory category, string name, ulong id)
    {
        WriteCorrelatedEvent(TraceEventKind.FlowStart, category, name, id, null);
    }

    /// <summary>Records an intermediate step in a flow.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="name">The event name. Its string reference is retained without copying.</param>
    /// <param name="id">The correlation identifier.</param>
    public static void FlowStep(TraceCategory category, string name, ulong id)
    {
        WriteCorrelatedEvent(TraceEventKind.FlowStep, category, name, id, null);
    }

    /// <summary>Records the end of a flow.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="name">The event name. Its string reference is retained without copying.</param>
    /// <param name="id">The correlation identifier.</param>
    public static void FlowEnd(TraceCategory category, string name, ulong id)
    {
        WriteCorrelatedEvent(TraceEventKind.FlowEnd, category, name, id, null);
    }

    /// <summary>Records the beginning of a flow with structured properties.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="messageTemplate">The Serilog-style message template retained as the event name.</param>
    /// <param name="id">The correlation identifier.</param>
    /// <param name="propertyValues">The values matched to template properties.</param>
    /// <exception cref="ArgumentNullException">The enabled call has a null message template.</exception>
    /// <exception cref="FormatException">The enabled call has a malformed message template.</exception>
    /// <exception cref="ArgumentException">The enabled call has mismatched template properties and values.</exception>
    /// <remarks>Serialization occurs only when the category is enabled, and serialization errors propagate.</remarks>
    public static void FlowStartSlow(
        TraceCategory category,
        string messageTemplate,
        ulong id,
        params object?[]? propertyValues)
    {
        WriteCorrelatedEventSlow(TraceEventKind.FlowStart, category, messageTemplate, id, propertyValues);
    }

    /// <summary>Records an intermediate step in a flow with structured properties.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="messageTemplate">The Serilog-style message template retained as the event name.</param>
    /// <param name="id">The correlation identifier.</param>
    /// <param name="propertyValues">The values matched to template properties.</param>
    /// <exception cref="ArgumentNullException">The enabled call has a null message template.</exception>
    /// <exception cref="FormatException">The enabled call has a malformed message template.</exception>
    /// <exception cref="ArgumentException">The enabled call has mismatched template properties and values.</exception>
    /// <remarks>Serialization occurs only when the category is enabled, and serialization errors propagate.</remarks>
    public static void FlowStepSlow(
        TraceCategory category,
        string messageTemplate,
        ulong id,
        params object?[]? propertyValues)
    {
        WriteCorrelatedEventSlow(TraceEventKind.FlowStep, category, messageTemplate, id, propertyValues);
    }

    /// <summary>Records the end of a flow with structured properties.</summary>
    /// <param name="category">The event category.</param>
    /// <param name="messageTemplate">The Serilog-style message template retained as the event name.</param>
    /// <param name="id">The correlation identifier.</param>
    /// <param name="propertyValues">The values matched to template properties.</param>
    /// <exception cref="ArgumentNullException">The enabled call has a null message template.</exception>
    /// <exception cref="FormatException">The enabled call has a malformed message template.</exception>
    /// <exception cref="ArgumentException">The enabled call has mismatched template properties and values.</exception>
    /// <remarks>Serialization occurs only when the category is enabled, and serialization errors propagate.</remarks>
    public static void FlowEndSlow(
        TraceCategory category,
        string messageTemplate,
        ulong id,
        params object?[]? propertyValues)
    {
        WriteCorrelatedEventSlow(TraceEventKind.FlowEnd, category, messageTemplate, id, propertyValues);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetSession(
        TraceCategory category,
        [NotNullWhen(true)] out TraceSession? session)
    {
        session = Volatile.Read(ref s_activeSession);
        return session is not null && (session.Categories & category) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TraceThreadWriter GetWriter(TraceSession session)
    {
        TraceThreadWriter? writer = t_writer;
        if (writer is null || writer.SessionId != session.Id)
        {
            writer = CreateWriter(session);
        }

        return writer;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TraceThreadWriter CreateWriter(TraceSession session)
    {
        var writer = new TraceThreadWriter(session.Id);
        session.Register(writer);
        t_writer = writer;
        return writer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TraceScope BeginScope(
        TraceSession session,
        TraceCategory category,
        string name,
        TracePayload? payload)
    {
        TraceRecordHandle handle = GetWriter(session).Reserve();
        long timestamp = Stopwatch.GetTimestamp();
        handle.Initialize(
            TraceEventKind.Complete,
            category,
            name,
            timestamp,
            -1,
            0,
            default,
            default,
            payload);
        return new TraceScope(handle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteEvent(
        TraceSession session,
        TraceEventKind kind,
        TraceCategory category,
        string name,
        ulong id,
        TraceInstantScope instantScope,
        TraceCounterValue counterValue,
        TracePayload? payload)
    {
        TraceRecordHandle handle = GetWriter(session).Reserve();
        long timestamp = Stopwatch.GetTimestamp();
        handle.Initialize(kind, category, name, timestamp, 0, id, instantScope, counterValue, payload);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteCorrelatedEvent(
        TraceEventKind kind,
        TraceCategory category,
        string name,
        ulong id,
        TracePayload? payload)
    {
        if (!TryGetSession(category, out TraceSession? session))
        {
            return;
        }

        WriteEvent(session, kind, category, name, id, default, default, payload);
    }

    private static void WriteCorrelatedEventSlow(
        TraceEventKind kind,
        TraceCategory category,
        string messageTemplate,
        ulong id,
        object?[]? propertyValues)
    {
        if (!TryGetSession(category, out TraceSession? session))
        {
            return;
        }

        TracePayload payload = TracePayload.Create(messageTemplate, propertyValues);
        WriteEvent(session, kind, category, messageTemplate, id, default, default, payload);
    }
}

internal sealed class TraceSession
{
    private readonly Lock _writerLock = new();
    private readonly List<TraceThreadWriter> _writers = [];

    internal TraceSession(long id, TraceCategory categories, long startTimestamp)
    {
        Id = id;
        Categories = categories;
        StartTimestamp = startTimestamp;
    }

    internal long Id { get; }

    internal TraceCategory Categories { get; }

    internal long StartTimestamp { get; }

    internal void Register(TraceThreadWriter writer)
    {
        lock (_writerLock)
        {
            _writers.Add(writer);
        }
    }

    internal TraceCapture CreateCapture(long endTimestamp)
    {
        TraceThreadWriter[] writers;
        lock (_writerLock)
        {
            writers = [.. _writers];
            _writers.Clear();
        }

        int recordCount = 0;
        foreach (TraceThreadWriter writer in writers)
        {
            recordCount += writer.RecordCount;
        }

        var events = new TraceEvent[recordCount];
        var threads = new TraceThreadInfo[writers.Length];
        int eventIndex = 0;

        for (int writerIndex = 0; writerIndex < writers.Length; ++writerIndex)
        {
            TraceThreadWriter writer = writers[writerIndex];
            threads[writerIndex] = new TraceThreadInfo(writer.ThreadId, writer.ThreadName);
            writer.CopyEvents(events, ref eventIndex, StartTimestamp);
            writer.Release();
        }

        if (eventIndex != events.Length)
        {
            Array.Resize(ref events, eventIndex);
        }

        Array.Sort(events, TraceEventComparer.Instance);
        Array.Sort(threads, TraceThreadInfoComparer.Instance);

        string? processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        if (string.IsNullOrEmpty(processName))
        {
            processName = AppDomain.CurrentDomain.FriendlyName;
        }

        return new TraceCapture(
            Environment.ProcessId,
            processName,
            Stopwatch.Frequency,
            Math.Max(0, endTimestamp - StartTimestamp),
            events,
            threads);
    }
}

internal sealed class TraceThreadWriter
{
    private const int ChunkCapacity = 1024;

    private TraceChunk _currentChunk;
    private TraceChunk _firstChunk;
    private long _nextSequence;

    internal TraceThreadWriter(long sessionId)
    {
        SessionId = sessionId;
        ThreadId = Environment.CurrentManagedThreadId;
        ThreadName = Thread.CurrentThread.Name ?? $"Thread {ThreadId}";
        _currentChunk = new TraceChunk(ChunkCapacity);
        _firstChunk = _currentChunk;
    }

    internal long SessionId { get; }

    internal int ThreadId { get; }

    internal string ThreadName { get; }

    internal int RecordCount { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal TraceRecordHandle Reserve()
    {
        if (_currentChunk.Count == ChunkCapacity)
        {
            Grow();
        }

        int index = _currentChunk.Count++;
        ++RecordCount;
        return new TraceRecordHandle(_currentChunk.Records, index, _nextSequence++);
    }

    internal void CopyEvents(TraceEvent[] destination, ref int destinationIndex, long startTimestamp)
    {
        for (TraceChunk? chunk = _firstChunk; chunk is not null; chunk = chunk.Next)
        {
            for (int index = 0; index < chunk.Count; ++index)
            {
                ref TraceRecord record = ref chunk.Records[index];
                if (record._kind == TraceEventKind.Complete && record._durationTicks < 0)
                {
                    Debug.Assert(false, "A trace scope was not completed before Trace.Stop().");
                    continue;
                }

                destination[destinationIndex++] = new TraceEvent(
                    record._kind,
                    record._category,
                    record._name!,
                    Math.Max(0, record._timestampTicks - startTimestamp),
                    record._durationTicks,
                    ThreadId,
                    record._id,
                    record._instantScope,
                    record._counterValue,
                    record._payload?.Arguments,
                    record._threadSequence);
            }
        }
    }

    internal void Release()
    {
        TraceChunk? chunk = _firstChunk;
        while (chunk is not null)
        {
            TraceChunk? next = chunk.Next;
            chunk.Release();
            chunk = next;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow()
    {
        var chunk = new TraceChunk(ChunkCapacity);
        _currentChunk.Next = chunk;
        _currentChunk = chunk;
    }
}

internal sealed class TraceChunk
{
    internal TraceChunk(int capacity)
    {
        Records = ArrayPool<TraceRecord>.Shared.Rent(capacity);
    }

    internal TraceRecord[] Records { get; private set; }

    internal int Count { get; set; }

    internal TraceChunk? Next { get; set; }

    internal void Release()
    {
        TraceRecord[] records = Records;
        Records = [];
        Next = null;
        Count = 0;
        ArrayPool<TraceRecord>.Shared.Return(records, true);
    }
}

internal struct TraceRecord
{
    internal TraceEventKind _kind;
    internal TraceCategory _category;
    internal string? _name;
    internal long _timestampTicks;
    internal long _durationTicks;
    internal ulong _id;
    internal TraceInstantScope _instantScope;
    internal TraceCounterValue _counterValue;
    internal TracePayload? _payload;
    internal long _threadSequence;
}

internal readonly struct TraceRecordHandle
{
    private readonly TraceRecord[]? _records;
    private readonly int _index;
    private readonly long _threadSequence;

    internal TraceRecordHandle(TraceRecord[] records, int index, long threadSequence)
    {
        _records = records;
        _index = index;
        _threadSequence = threadSequence;
    }

    internal void Initialize(
        TraceEventKind kind,
        TraceCategory category,
        string name,
        long timestampTicks,
        long durationTicks,
        ulong id,
        TraceInstantScope instantScope,
        TraceCounterValue counterValue,
        TracePayload? payload)
    {
        ref TraceRecord record = ref _records![_index];
        record._kind = kind;
        record._category = category;
        record._name = name;
        record._timestampTicks = timestampTicks;
        record._durationTicks = durationTicks;
        record._id = id;
        record._instantScope = instantScope;
        record._counterValue = counterValue;
        record._payload = payload;
        record._threadSequence = _threadSequence;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Complete(long endTimestamp)
    {
        ref TraceRecord record = ref _records![_index];
        record._durationTicks = Math.Max(0, endTimestamp - record._timestampTicks);
    }
}

internal sealed class TraceEventComparer : IComparer<TraceEvent>
{
    internal static TraceEventComparer Instance { get; } = new();

    public int Compare(TraceEvent left, TraceEvent right)
    {
        int timestampComparison = left.TimestampTicks.CompareTo(right.TimestampTicks);
        if (timestampComparison != 0)
        {
            return timestampComparison;
        }

        int threadComparison = left.ThreadId.CompareTo(right.ThreadId);
        if (threadComparison != 0)
        {
            return threadComparison;
        }

        return left.ThreadSequence.CompareTo(right.ThreadSequence);
    }
}

internal sealed class TraceThreadInfoComparer : IComparer<TraceThreadInfo>
{
    internal static TraceThreadInfoComparer Instance { get; } = new();

    public int Compare(TraceThreadInfo left, TraceThreadInfo right)
    {
        return left.Id.CompareTo(right.Id);
    }
}
