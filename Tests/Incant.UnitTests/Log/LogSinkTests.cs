using Incant.Base.Log;
using LogRecorder = Incant.Base.Log.Log;

namespace Incant.UnitTests.Log;

[Collection(LogCollection.Name)]
public sealed class LogSinkTests : LogTestBase
{
    [Fact]
    public void CliSinkWritesOnlyMessageContentByDefault()
    {
        var writer = new StringWriter();
        var sink = new CliLogSink(writer, colorMode: CliColorMode.Never);
        Start(sink);

        LogRecorder.Info(LogCategory.Build, "Building {Target}", "sample");
        LogRecorder.Stop();

        Assert.Equal($"Building sample{Environment.NewLine}", writer.ToString());
    }

    [Fact]
    public void CliSinkCanIncludeLevelAndCategoryPrefix()
    {
        var writer = new StringWriter();
        var sink = new CliLogSink(
            writer,
            colorMode: CliColorMode.Never,
            includePrefix: true);
        Start(sink);

        LogRecorder.Info(LogCategory.Build, "Building {Target}", "sample");
        LogRecorder.Stop();

        Assert.Equal($"[INF] [Build] Building sample{Environment.NewLine}", writer.ToString());
    }

    [Fact]
    public void CliSinkColorsAnIncludedPrefixWithoutStylingTheMessageFromItsLevel()
    {
        var writer = new StringWriter();
        var sink = new CliLogSink(
            writer,
            colorMode: CliColorMode.Always,
            includePrefix: true);
        Start(sink);

        LogRecorder.Warning("message");
        LogRecorder.Stop();

        Assert.Equal(
            $"\u001b[33m[WRN]\u001b[0m\u001b[90m [General]\u001b[0m message\u001b[0m{Environment.NewLine}",
            writer.ToString());
    }

    [Fact]
    public void CliSinkDefaultsToInfoLevelIndependentlyFromGlobalFiltering()
    {
        var writer = new StringWriter();
        var sink = new CliLogSink(writer, colorMode: CliColorMode.Never);
        Start(sink);

        LogRecorder.Trace("trace");
        LogRecorder.Debug("debug");
        LogRecorder.Info("info");
        LogRecorder.Warning("warning");
        LogRecorder.Stop();

        Assert.Equal(LogLevel.Info, sink.MinimumLevel);
        Assert.Equal(
            $"info{Environment.NewLine}warning{Environment.NewLine}",
            writer.ToString());
    }

    [Fact]
    public void CliSinkMinimumLevelCanBeConfiguredIndependently()
    {
        var writer = new StringWriter();
        var sink = new CliLogSink(
            writer,
            minimumLevel: LogLevel.Trace,
            colorMode: CliColorMode.Never);
        Start(sink);

        LogRecorder.Trace("trace");
        LogRecorder.Debug("debug");
        LogRecorder.Stop();

        Assert.Equal(
            $"trace{Environment.NewLine}debug{Environment.NewLine}",
            writer.ToString());
    }

    [Fact]
    public void CliAutoModeDoesNotColorCallerOwnedWriters()
    {
        var writer = new StringWriter();
        var sink = new CliLogSink(writer, colorMode: CliColorMode.Auto);
        Start(sink);

        LogRecorder.Warning("warning");
        LogRecorder.Stop();

        Assert.DoesNotContain("\u001b[", writer.ToString(), StringComparison.Ordinal);
        Assert.Equal($"warning{Environment.NewLine}", writer.ToString());
    }

    [Fact]
    public void CliSinkRestoresParentStyleAfterNestedParameterDecoration()
    {
        var writer = new StringWriter();
        var sink = new CliLogSink(writer, colorMode: CliColorMode.Always);
        Start(sink);

        LogRecorder.Info(
            Text.Warning(),
            "root {Value} tail",
            Param.Important("strong"));
        LogRecorder.Stop();

        string output = writer.ToString();
        Assert.Contains("\u001b[0m\u001b[1;33mroot ", output, StringComparison.Ordinal);
        Assert.Contains(
            "\u001b[0m\u001b[1;32mstrong\u001b[0m\u001b[1;33m tail",
            output,
            StringComparison.Ordinal);
        Assert.EndsWith($"\u001b[0m\u001b[0m{Environment.NewLine}", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Role.Plain, "")]
    [InlineData(Role.Muted, "\u001b[90m")]
    [InlineData(Role.Important, "\u001b[1;32m")]
    [InlineData(Role.Warning, "\u001b[1;33m")]
    [InlineData(Role.Error, "\u001b[1;31m")]
    [InlineData(Role.Label, "\u001b[1;35m")]
    public void CliSinkRendersStandardRolesForTextAndParameters(Role role, string style)
    {
        var writer = new StringWriter();
        var sink = new CliLogSink(writer, colorMode: CliColorMode.Always);
        Start(sink);

        LogRecorder.Info(
            "{#Scope}scope{/Scope} {Value}",
            new TextDecoratorRole(role),
            Param.Role(role, "value"));
        LogRecorder.Stop();

        Assert.Contains(
            $"\u001b[0m{style}scope\u001b[0m \u001b[0m{style}value\u001b[0m",
            writer.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CliSinkIgnoresCustomDecoratorsAndDoesNotStyleTheBodyFromTheLevel()
    {
        var writer = new StringWriter();
        var sink = new CliLogSink(writer, colorMode: CliColorMode.Always);
        Start(sink);

        LogRecorder.Warning(
            "{#Custom}plain{/Custom} {Value}",
            new CustomTextDecorator(),
            new CustomParamDecorator("value"));
        LogRecorder.Stop();

        Assert.Equal(
            $"plain value\u001b[0m{Environment.NewLine}",
            writer.ToString());
    }

    [Fact]
    public void CliSinkContinuesRoleRenderingAfterCustomDecoratorNodes()
    {
        var writer = new StringWriter();
        var sink = new CliLogSink(writer, colorMode: CliColorMode.Always);
        Start(sink);

        LogRecorder.Info(
            "{#Custom}label{/Custom} {Value}",
            new CustomTextDecorator(new TextDecoratorRole(Role.Label)),
            new CustomParamDecorator(Param.Important("important")));
        LogRecorder.Stop();

        Assert.Contains(
            "\u001b[0m\u001b[1;35mlabel\u001b[0m \u001b[0m\u001b[1;32mimportant\u001b[0m",
            writer.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FailingSinkIsDisabledWhileOtherSinksContinue()
    {
        var error = new StringWriter();
        TextWriter previousError = Console.Error;
        var healthySink = new CollectingLogSink();
        var failingSink = new ThrowingSink();
        try
        {
            Console.SetError(error);
            LogRecorder.MinimumLevel = LogLevel.Trace;
            LogRecorder.AddSink(failingSink);
            LogRecorder.AddSink(healthySink);
            LogRecorder.Start(new LogOptions());

            LogRecorder.Info("first");
            LogRecorder.Info("second");
            LogRecorder.Stop();
        }
        finally
        {
            Console.SetError(previousError);
        }

        Assert.Equal(["first", "second"], healthySink.Events.Select(logEvent => logEvent.Message));
        Assert.Equal(1, failingSink.EmitCount);
        Assert.True(failingSink.IsDisposed);
        Assert.Contains(nameof(ThrowingSink), error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeCallsEverySinkMethodOnOneDedicatedWorker()
    {
        int callingThreadId = Environment.CurrentManagedThreadId;
        var sink = new ThreadRecordingSink();
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.Start(new LogOptions());
        LogRecorder.AddSink(sink);

        LogRecorder.Info("event");
        LogRecorder.Flush();
        Assert.True(LogRecorder.RemoveSink(sink));
        LogRecorder.Stop();

        Assert.NotEmpty(sink.ThreadIds);
        int workerThreadId = sink.ThreadIds[0];
        Assert.NotEqual(callingThreadId, workerThreadId);
        Assert.All(sink.ThreadIds, threadId => Assert.Equal(workerThreadId, threadId));
    }

    [Fact]
    public void FailedDynamicRegistrationLeavesOwnershipWithTheCaller()
    {
        var healthySink = new CollectingLogSink();
        var failingSink = new StartThrowingSink();
        Start(healthySink);

        Assert.Throws<InvalidOperationException>(() => LogRecorder.AddSink(failingSink));
        Assert.DoesNotContain(failingSink, LogRecorder.Sinks);
        Assert.False(failingSink.IsDisposed);
        LogRecorder.Info("still running");
        LogRecorder.Stop();

        Assert.False(failingSink.IsDisposed);
        Assert.Equal("still running", Assert.Single(healthySink.Events).Message);
    }

    [Fact]
    public void FailedInitialSinkStartDisposesRegisteredSinksAndLeavesLoggingStopped()
    {
        var firstSink = new CollectingLogSink();
        var failingSink = new StartThrowingSink();
        var lastSink = new CollectingLogSink();
        LogRecorder.AddSink(firstSink);
        LogRecorder.AddSink(failingSink);
        LogRecorder.AddSink(lastSink);

        Assert.Throws<InvalidOperationException>(() => LogRecorder.Start(new LogOptions()));

        Assert.False(LogRecorder.IsRunning);
        Assert.Empty(LogRecorder.Sinks);
        Assert.True(firstSink.IsDisposed);
        Assert.True(failingSink.IsDisposed);
        Assert.True(lastSink.IsDisposed);
    }

    private static void Start(ILogSink sink)
    {
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(sink);
        LogRecorder.Start(new LogOptions());
    }

    private sealed class ThrowingSink : ILogSink
    {
        public LogLevel MinimumLevel => LogLevel.Trace;

        internal int EmitCount { get; private set; }

        internal bool IsDisposed { get; private set; }

        public void Start(LogSinkContext context)
        {
        }

        public void Emit(RenderedLogEvent logEvent)
        {
            ++EmitCount;
            throw new UndescribableException();
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
            IsDisposed = true;
        }

        private sealed class UndescribableException : Exception
        {
            public override string ToString()
            {
                throw new InvalidOperationException("exception rendering failed");
            }
        }
    }

    private sealed class ThreadRecordingSink : ILogSink
    {
        private readonly List<int> _threadIds = [];

        public LogLevel MinimumLevel => LogLevel.Trace;

        internal IReadOnlyList<int> ThreadIds => _threadIds;

        public void Start(LogSinkContext context)
        {
            RecordThread();
        }

        public void Emit(RenderedLogEvent logEvent)
        {
            RecordThread();
        }

        public void Flush()
        {
            RecordThread();
        }

        public void Dispose()
        {
            RecordThread();
        }

        private void RecordThread()
        {
            _threadIds.Add(Environment.CurrentManagedThreadId);
        }
    }

    private sealed class StartThrowingSink : ILogSink
    {
        public LogLevel MinimumLevel => LogLevel.Trace;

        internal bool IsDisposed { get; private set; }

        public void Start(LogSinkContext context)
        {
            throw new InvalidOperationException("Sink startup failed.");
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

    private sealed class CustomTextDecorator(TextDecorator? next = null) : TextDecorator(next)
    {
    }

    private sealed class CustomParamDecorator(object? next) : ParamDecorator(next)
    {
    }
}
