namespace Incant.Base.Log;

/// <summary>Configures the fixed resources of a process-wide log runtime.</summary>
public sealed class LogOptions
{
    /// <summary>Gets or initializes the power-of-two capacity of each thread-local queue, from 2 through 65536.</summary>
    public int QueueCapacityPerThread { get; init; } = 256;

    /// <summary>Gets or initializes the positive periodic sink flush interval, up to one day.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>Provides immutable process metadata to a sink when logging starts.</summary>
public sealed class LogSinkContext
{
    internal LogSinkContext(int processId, string processName, long timestampFrequency)
    {
        ProcessId = processId;
        ProcessName = processName;
        TimestampFrequency = timestampFrequency;
    }

    /// <summary>Gets the operating-system process identifier.</summary>
    public int ProcessId { get; }

    /// <summary>Gets the process name.</summary>
    public string ProcessName { get; }

    /// <summary>Gets the number of monotonic timestamp ticks per second.</summary>
    public long TimestampFrequency { get; }
}

/// <summary>Consumes rendered events on the dedicated log worker thread.</summary>
/// <remarks>
/// While attached to a running logger, all members are called serially by its worker. Implementations must return
/// promptly and must not call back into <see cref="Log"/>.
/// </remarks>
public interface ILogSink : IDisposable
{
    /// <summary>Gets the minimum level accepted by this sink.</summary>
    LogLevel MinimumLevel { get; }

    /// <summary>Initializes the sink before it starts receiving events.</summary>
    /// <param name="context">Process metadata for the runtime.</param>
    void Start(LogSinkContext context);

    /// <summary>Consumes one immutable rendered event.</summary>
    /// <param name="logEvent">The event to consume.</param>
    void Emit(RenderedLogEvent logEvent);

    /// <summary>Flushes buffered output.</summary>
    void Flush();
}
