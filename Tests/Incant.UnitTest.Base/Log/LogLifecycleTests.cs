using Incant.Base.Log;
using LogRecorder = Incant.Base.Log.Log;

namespace Incant.UnitTest.Base.Log;

[Collection(LogCollection.Name)]
public sealed class LogLifecycleTests : LogTestBase
{
    [Fact]
    public void StartFlushAndStopExposeStateAndTransferSinkOwnership()
    {
        var sink = new CollectingLogSink();
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(sink);

        LogRecorder.Start(new LogOptions());
        Assert.True(LogRecorder.IsRunning);
        Assert.True(LogRecorder.IsEnabled(LogLevel.Info));

        LogRecorder.Info(LogCategory.Build, "Building {Target}", "sample");
        LogRecorder.Flush();

        RenderedLogEvent logEvent = Assert.Single(sink.Events);
        Assert.Equal(Environment.ProcessId, logEvent.ProcessId);
        Assert.Equal(LogCategory.Build, logEvent.Category);
        Assert.Equal("Building sample", logEvent.Message);
        Assert.True(sink.FlushCount > 0);

        LogRecorder.Stop();

        Assert.False(LogRecorder.IsRunning);
        Assert.Empty(LogRecorder.Sinks);
        Assert.True(sink.IsDisposed);
        Assert.Equal(1, sink.StartCount);
    }

    [Fact]
    public void InvalidLifecycleCallsThrowWithoutPoisoningLaterRuns()
    {
        Assert.Throws<InvalidOperationException>(() => LogRecorder.Flush());
        Assert.Throws<InvalidOperationException>(() => LogRecorder.Stop());

        var sink = new CollectingLogSink();
        StartWithSink(sink);
        Assert.Throws<InvalidOperationException>(() => LogRecorder.Start(new LogOptions()));
        LogRecorder.Stop();

        var nextSink = new CollectingLogSink();
        StartWithSink(nextSink);
        LogRecorder.Info("next run");
        LogRecorder.Stop();

        Assert.Equal("next run", Assert.Single(nextSink.Events).Message);
    }

    [Fact]
    public void SinksPropertyReturnsStableReadOnlySnapshotsAndDuplicateRegistrationIsRejected()
    {
        var firstSink = new CollectingLogSink();
        var secondSink = new CollectingLogSink();
        LogRecorder.AddSink(firstSink);
        IReadOnlyList<ILogSink> firstSnapshot = LogRecorder.Sinks;

        Assert.Throws<InvalidOperationException>(() => LogRecorder.AddSink(firstSink));
        LogRecorder.AddSink(secondSink);

        Assert.Same(firstSink, Assert.Single(firstSnapshot));
        Assert.Equal(2, LogRecorder.Sinks.Count);
        Assert.Same(firstSink, LogRecorder.Sinks[0]);
        Assert.Same(secondSink, LogRecorder.Sinks[1]);
    }

    [Fact]
    public void ConsecutiveRunsRemainIndependent()
    {
        var firstSink = new CollectingLogSink();
        StartWithSink(firstSink);
        LogRecorder.Info("first");
        LogRecorder.Stop();

        var secondSink = new CollectingLogSink();
        StartWithSink(secondSink);
        LogRecorder.Info("second");
        LogRecorder.Stop();

        Assert.Equal("first", Assert.Single(firstSink.Events).Message);
        Assert.Equal("second", Assert.Single(secondSink.Events).Message);
    }

    [Fact]
    public void MinimumLevelCanChangeBeforeDuringAndAfterARun()
    {
        var detailedSink = new CollectingLogSink(LogLevel.Trace);
        var warningSink = new CollectingLogSink(LogLevel.Warning);
        LogRecorder.MinimumLevel = LogLevel.Info;
        LogRecorder.AddSink(detailedSink);
        LogRecorder.AddSink(warningSink);
        LogRecorder.Start(new LogOptions());

        LogRecorder.Debug("filtered debug");
        LogRecorder.Info("info");
        LogRecorder.Warning("warning");
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.Debug("accepted debug");
        LogRecorder.MinimumLevel = LogLevel.None;
        LogRecorder.Fatal("filtered fatal");
        LogRecorder.Stop();
        LogRecorder.MinimumLevel = LogLevel.Warning;

        Assert.Equal(LogLevel.Warning, LogRecorder.MinimumLevel);
        Assert.Equal(
            ["info", "warning", "accepted debug"],
            detailedSink.Events.Select(logEvent => logEvent.Message));
        Assert.Equal(["warning"], warningSink.Events.Select(logEvent => logEvent.Message));
    }

    [Fact]
    public void SinksCanBeAddedRemovedAndClearedWhileRunning()
    {
        var firstSink = new CollectingLogSink();
        var secondSink = new CollectingLogSink();
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.Start(new LogOptions());
        Assert.False(LogRecorder.IsEnabled(LogLevel.Fatal));

        LogRecorder.AddSink(firstSink);
        LogRecorder.Info("first");
        LogRecorder.AddSink(secondSink);
        LogRecorder.Info("second");
        Assert.True(LogRecorder.RemoveSink(firstSink));
        LogRecorder.Info("third");
        Assert.False(LogRecorder.RemoveSink(firstSink));
        LogRecorder.ClearSinks();
        LogRecorder.Info("ignored");
        LogRecorder.Stop();

        Assert.Equal(["first", "second"], firstSink.Events.Select(logEvent => logEvent.Message));
        Assert.Equal(["second", "third"], secondSink.Events.Select(logEvent => logEvent.Message));
        Assert.True(firstSink.IsDisposed);
        Assert.True(secondSink.IsDisposed);
        Assert.Empty(LogRecorder.Sinks);
    }

    [Fact]
    public void EffectiveLevelTracksPerSinkLevelsAsSinksChange()
    {
        var warningSink = new CollectingLogSink(LogLevel.Warning);
        var detailedSink = new CollectingLogSink(LogLevel.Trace);
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.Start(new LogOptions());

        LogRecorder.AddSink(warningSink);
        Assert.False(LogRecorder.IsEnabled(LogLevel.Info));
        Assert.True(LogRecorder.IsEnabled(LogLevel.Warning));

        LogRecorder.AddSink(detailedSink);
        Assert.True(LogRecorder.IsEnabled(LogLevel.Info));

        Assert.True(LogRecorder.RemoveSink(detailedSink));
        Assert.False(LogRecorder.IsEnabled(LogLevel.Info));
        Assert.True(LogRecorder.IsEnabled(LogLevel.Warning));
    }

    [Fact]
    public void SinkCanBeRemovedBeforeStartAndAddedAfterStop()
    {
        var removedSink = new CollectingLogSink();
        LogRecorder.AddSink(removedSink);

        Assert.True(LogRecorder.RemoveSink(removedSink));
        Assert.True(removedSink.IsDisposed);
        Assert.Equal(0, removedSink.StartCount);

        LogRecorder.Start(new LogOptions());
        LogRecorder.Stop();

        var nextSink = new CollectingLogSink();
        LogRecorder.AddSink(nextSink);
        LogRecorder.Start(new LogOptions());
        LogRecorder.Info("next");
        LogRecorder.Stop();

        Assert.Equal("next", Assert.Single(nextSink.Events).Message);
    }

    [Fact]
    public void DisabledEventsDoNotEvaluateParametersOrRegisterOutput()
    {
        var sink = new CollectingLogSink();
        LogRecorder.MinimumLevel = LogLevel.Info;
        LogRecorder.AddSink(sink);
        LogRecorder.Start(new LogOptions());

        LogRecorder.Debug("Ignored {Value}", new ThrowingStringValue());
        LogRecorder.Info("accepted");
        LogRecorder.Stop();

        RenderedLogEvent logEvent = Assert.Single(sink.Events);
        Assert.Equal("accepted", logEvent.Message);
    }

    [Fact]
    public void OptionsAllowNoSinksAndRejectInvalidQueueCapacity()
    {
        LogRecorder.Start(new LogOptions());
        Assert.True(LogRecorder.IsRunning);
        Assert.False(LogRecorder.IsEnabled(LogLevel.Fatal));
        LogRecorder.Stop();

        var sink = new CollectingLogSink();
        LogRecorder.AddSink(sink);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LogRecorder.Start(new LogOptions { QueueCapacityPerThread = 3 }));

        Assert.False(LogRecorder.IsRunning);
        Assert.Same(sink, Assert.Single(LogRecorder.Sinks));
        Assert.False(sink.IsDisposed);
    }

    [Fact]
    public void SinkAndMinimumLevelApisRejectInvalidInputWithoutTakingOwnership()
    {
        var sink = new InvalidLevelSink();

        Assert.Throws<ArgumentNullException>(() => LogRecorder.AddSink(null!));
        Assert.Throws<ArgumentNullException>(() => LogRecorder.RemoveSink(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => LogRecorder.AddSink(sink));
        Assert.Throws<ArgumentOutOfRangeException>(() => LogRecorder.MinimumLevel = (LogLevel)100);

        Assert.False(sink.IsDisposed);
        Assert.Empty(LogRecorder.Sinks);
        Assert.Equal(LogLevel.Info, LogRecorder.MinimumLevel);
    }

    [Fact]
    public void CategoriesAreOrdinalNamedValuesWithValidatedSyntax()
    {
        var first = new LogCategory("Custom.Build");
        var second = new LogCategory("Custom.Build");
        var differentCase = new LogCategory("custom.build");

        Assert.Equal(first, second);
        Assert.NotEqual(first, differentCase);
        Assert.Equal(LogCategory.General, default(LogCategory));
        Assert.Equal("Custom.Build", first.ToString());
        Assert.Throws<ArgumentException>(() => new LogCategory(string.Empty));
        Assert.Throws<ArgumentException>(() => new LogCategory("Bad[Category"));
        Assert.Throws<ArgumentException>(() => new LogCategory("Bad\nCategory"));
    }

    private static void StartWithSink(ILogSink sink)
    {
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(sink);
        LogRecorder.Start(new LogOptions());
    }

    private sealed class ThrowingStringValue
    {
        public override string ToString()
        {
            throw new InvalidOperationException("A disabled log argument must not be captured.");
        }
    }

    private sealed class InvalidLevelSink : ILogSink
    {
        public LogLevel MinimumLevel => (LogLevel)100;

        internal bool IsDisposed { get; private set; }

        public void Start(LogSinkContext context)
        {
        }

        public void Emit(RenderedLogEvent logEvent)
        {
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
