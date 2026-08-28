using System.Text.Json;

namespace Incant.Base.Trace;

/// <summary>Identifies the kind of a captured trace event.</summary>
public enum TraceEventKind
{
    /// <summary>A synchronous duration event.</summary>
    Complete,

    /// <summary>An instantaneous event.</summary>
    Instant,

    /// <summary>A numeric counter sample.</summary>
    Counter,

    /// <summary>The beginning of an asynchronous operation.</summary>
    AsyncBegin,

    /// <summary>An instantaneous event within an asynchronous operation.</summary>
    AsyncEvent,

    /// <summary>The end of an asynchronous operation.</summary>
    AsyncEnd,

    /// <summary>The beginning of a flow.</summary>
    FlowStart,

    /// <summary>An intermediate step in a flow.</summary>
    FlowStep,

    /// <summary>The end of a flow.</summary>
    FlowEnd,
}

/// <summary>Identifies the visibility of an instantaneous trace event.</summary>
public enum TraceInstantScope
{
    /// <summary>The event is visible on its originating thread.</summary>
    Thread,

    /// <summary>The event is visible across its process.</summary>
    Process,

    /// <summary>The event is globally visible.</summary>
    Global,
}

/// <summary>Identifies the numeric representation of a counter value.</summary>
public enum TraceCounterValueKind
{
    /// <summary>The event does not contain a counter value.</summary>
    None,

    /// <summary>The counter contains a signed integer.</summary>
    Signed,

    /// <summary>The counter contains an unsigned integer.</summary>
    Unsigned,

    /// <summary>The counter contains a floating-point value.</summary>
    FloatingPoint,
}

/// <summary>Stores a numeric counter value without boxing it.</summary>
public readonly struct TraceCounterValue
{
    internal TraceCounterValue(long value)
    {
        Kind = TraceCounterValueKind.Signed;
        SignedValue = value;
    }

    internal TraceCounterValue(ulong value)
    {
        Kind = TraceCounterValueKind.Unsigned;
        UnsignedValue = value;
    }

    internal TraceCounterValue(double value)
    {
        Kind = TraceCounterValueKind.FloatingPoint;
        FloatingPointValue = value;
    }

    /// <summary>Gets the representation used by this counter value.</summary>
    public TraceCounterValueKind Kind { get; }

    /// <summary>Gets the signed value when <see cref="Kind"/> is <see cref="TraceCounterValueKind.Signed"/>.</summary>
    public long SignedValue { get; }

    /// <summary>Gets the unsigned value when <see cref="Kind"/> is <see cref="TraceCounterValueKind.Unsigned"/>.</summary>
    public ulong UnsignedValue { get; }

    /// <summary>Gets the floating-point value when <see cref="Kind"/> is <see cref="TraceCounterValueKind.FloatingPoint"/>.</summary>
    public double FloatingPointValue { get; }
}

/// <summary>Represents an immutable event in a completed trace capture.</summary>
public readonly struct TraceEvent
{
    internal TraceEvent(
        TraceEventKind kind,
        TraceCategory category,
        string name,
        long timestampTicks,
        long durationTicks,
        int threadId,
        ulong id,
        TraceInstantScope instantScope,
        TraceCounterValue counterValue,
        JsonElement? arguments,
        long threadSequence)
    {
        Kind = kind;
        Category = category;
        Name = name;
        TimestampTicks = timestampTicks;
        DurationTicks = durationTicks;
        ThreadId = threadId;
        Id = id;
        InstantScope = instantScope;
        CounterValue = counterValue;
        Arguments = arguments;
        ThreadSequence = threadSequence;
    }

    /// <summary>Gets the event kind.</summary>
    public TraceEventKind Kind { get; }

    /// <summary>Gets the event category.</summary>
    public TraceCategory Category { get; }

    /// <summary>Gets the event name.</summary>
    public string Name { get; }

    /// <summary>Gets the timestamp relative to the beginning of the capture.</summary>
    public long TimestampTicks { get; }

    /// <summary>Gets the duration of a complete event, or zero for other event kinds.</summary>
    public long DurationTicks { get; }

    /// <summary>Gets the managed thread identifier that recorded the event.</summary>
    public int ThreadId { get; }

    /// <summary>Gets the correlation identifier used by asynchronous and flow events.</summary>
    public ulong Id { get; }

    /// <summary>Gets the visibility of an instantaneous event.</summary>
    public TraceInstantScope InstantScope { get; }

    /// <summary>Gets the numeric value of a counter event.</summary>
    public TraceCounterValue CounterValue { get; }

    /// <summary>Gets an immutable JSON snapshot of the event arguments, when present.</summary>
    public JsonElement? Arguments { get; }

    internal long ThreadSequence { get; }
}

/// <summary>Describes a managed thread that contributed events to a capture.</summary>
public readonly struct TraceThreadInfo
{
    internal TraceThreadInfo(int id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>Gets the managed thread identifier.</summary>
    public int Id { get; }

    /// <summary>Gets the thread name captured when the thread first emitted an event.</summary>
    public string Name { get; }
}
