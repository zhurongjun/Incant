using System.Diagnostics;

namespace Incant.Base.Trace;

/// <summary>Completes a synchronous trace scope when disposed.</summary>
/// <remarks>
/// A scope must be disposed synchronously on the thread that created it. Use the asynchronous event APIs for
/// logical work that crosses an <see langword="await"/> boundary.
/// </remarks>
public ref struct TraceScope
{
    private TraceRecordHandle _handle;
    private bool _isActive;

    internal TraceScope(TraceRecordHandle handle)
    {
        _handle = handle;
        _isActive = true;
    }

    /// <summary>Completes the scope.</summary>
    public void Dispose()
    {
        if (!_isActive)
        {
            return;
        }

        long endTimestamp = Stopwatch.GetTimestamp();
        _handle.Complete(endTimestamp);
        _isActive = false;
    }
}
