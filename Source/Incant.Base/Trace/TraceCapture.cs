namespace Incant.Base.Trace;

/// <summary>Contains the immutable result of a completed trace session.</summary>
public sealed class TraceCapture
{
    private readonly TraceEvent[] _events;
    private readonly TraceThreadInfo[] _threads;

    internal TraceCapture(
        int processId,
        string processName,
        long timestampFrequency,
        long durationTicks,
        TraceEvent[] events,
        TraceThreadInfo[] threads)
    {
        ProcessId = processId;
        ProcessName = processName;
        TimestampFrequency = timestampFrequency;
        DurationTicks = durationTicks;
        _events = events;
        _threads = threads;
    }

    /// <summary>Gets the operating-system process identifier.</summary>
    public int ProcessId { get; }

    /// <summary>Gets the process name.</summary>
    public string ProcessName { get; }

    /// <summary>Gets the number of timestamp ticks per second.</summary>
    public long TimestampFrequency { get; }

    /// <summary>Gets the total capture duration in timestamp ticks.</summary>
    public long DurationTicks { get; }

    /// <summary>Gets the events sorted by timestamp, thread identifier, and per-thread sequence.</summary>
    public ReadOnlyMemory<TraceEvent> Events => _events;

    /// <summary>Gets metadata for threads that contributed records to the capture.</summary>
    public ReadOnlyMemory<TraceThreadInfo> Threads => _threads;
}
