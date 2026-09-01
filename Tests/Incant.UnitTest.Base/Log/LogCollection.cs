using Incant.Base.Log;
using LogRecorder = Incant.Base.Log.Log;

namespace Incant.UnitTest.Base.Log;

internal static class LogCollection
{
    internal const string Name = "Log";
}

[CollectionDefinition(LogCollection.Name, DisableParallelization = true)]
public sealed class LogCollectionDefinition
{
}

public abstract class LogTestBase : IDisposable
{
    public void Dispose()
    {
        try
        {
            if (LogRecorder.IsRunning)
            {
                LogRecorder.Stop();
            }
        }
        finally
        {
            LogRecorder.ClearSinks();
            LogRecorder.MinimumLevel = LogLevel.Info;
        }

        GC.SuppressFinalize(this);
    }
}

internal sealed class CollectingLogSink : ILogSink
{
    private readonly Lock _eventLock = new();
    private readonly List<RenderedLogEvent> _events = [];

    internal CollectingLogSink(LogLevel minimumLevel = LogLevel.Trace)
    {
        MinimumLevel = minimumLevel;
    }

    public LogLevel MinimumLevel { get; }

    internal bool IsDisposed { get; private set; }

    internal int FlushCount { get; private set; }

    internal int StartCount { get; private set; }

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
        ArgumentNullException.ThrowIfNull(context);
        ++StartCount;
    }

    public void Emit(RenderedLogEvent logEvent)
    {
        lock (_eventLock)
        {
            _events.Add(logEvent);
        }
    }

    public void Flush()
    {
        ++FlushCount;
    }

    public void Dispose()
    {
        IsDisposed = true;
    }
}
