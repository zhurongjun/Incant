using TraceRecorder = Incant.Base.Trace.Trace;

namespace Incant.UnitTest.Base.Trace;

internal static class TraceCollection
{
    internal const string Name = "Trace";
}

[CollectionDefinition(TraceCollection.Name, DisableParallelization = true)]
public sealed class TraceCollectionDefinition
{
}

public abstract class TraceTestBase : IDisposable
{
    public void Dispose()
    {
        if (TraceRecorder.IsRunning)
        {
            TraceRecorder.Stop();
        }

        GC.SuppressFinalize(this);
    }
}
