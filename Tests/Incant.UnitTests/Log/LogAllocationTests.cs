using Incant.Base.Log;
using LogRecorder = Incant.Base.Log.Log;

namespace Incant.UnitTests.Log;

[Collection(LogCollection.Name)]
public sealed class LogAllocationTests : LogTestBase
{
    [Fact]
    public void DisabledGenericPathDoesNotAllocateAfterWarmup()
    {
        LogRecorder.MinimumLevel = LogLevel.Info;
        LogRecorder.AddSink(new NullLogSink());
        LogRecorder.Start(new LogOptions());
        LogRecorder.Debug("Ignored {Value}", 1);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_000; ++index)
        {
            LogRecorder.Debug("Ignored {Value}", index);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        LogRecorder.Stop();

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void EnabledFourPrimitiveArgumentPathDoesNotAllocateAfterRegistration()
    {
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(new NullLogSink());
        LogRecorder.Start(
            new LogOptions
            {
                QueueCapacityPerThread = 65_536,
                FlushInterval = TimeSpan.FromSeconds(30),
            });
        LogRecorder.Info("Warm {A} {B} {C} {D}", 1, 2, 3, 4);
        LogRecorder.Flush();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_000; ++index)
        {
            LogRecorder.Info("Values {A} {B} {C} {D}", index, 2, 3, 4);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        LogRecorder.Stop();

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void EnabledEnumPathDoesNotBoxAfterWarmup()
    {
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(new NullLogSink());
        LogRecorder.Start(
            new LogOptions
            {
                QueueCapacityPerThread = 65_536,
                FlushInterval = TimeSpan.FromSeconds(30),
            });
        LogRecorder.Info("State {State}", TestState.Ready);
        LogRecorder.Flush();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_000; ++index)
        {
            LogRecorder.Info("State {State}", TestState.Ready);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        LogRecorder.Stop();

        Assert.Equal(0, allocated);
    }

    private enum TestState
    {
        Ready,
    }
}
