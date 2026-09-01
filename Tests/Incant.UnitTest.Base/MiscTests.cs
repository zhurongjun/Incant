using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Incant.Base;
using Incant.Base.Log;
using Incant.Base.Trace;
using IncantLog = Incant.Base.Log.Log;
using IncantTrace = Incant.Base.Trace.Trace;

namespace Incant.UnitTest.Base;

public sealed class MiscTests
{
    [Fact]
    public void UniqueTempFileNameUsesHintMd5AndExtension()
    {
        string result = Misc.GetUniqueTempFileName(
            "compile",
            "obj",
            ["-O2", "DEBUG"]);

        Assert.Equal("compile_A9AEDE6B1213B84534EC10C75EDC1845.obj", result);
    }

    [Fact]
    public void UniqueTempFileNameConcatenatesArgumentsWithoutSeparators()
    {
        string first = Misc.GetUniqueTempFileName("compile", "obj", ["ab", "c"]);
        string second = Misc.GetUniqueTempFileName("compile", "obj", ["a", "bc"]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void UniqueTempFileNameRejectsInvalidRequiredValues()
    {
        Assert.Throws<ArgumentNullException>(() => Misc.GetUniqueTempFileName(null!, "ext"));
        Assert.Throws<ArgumentException>(() => Misc.GetUniqueTempFileName(string.Empty, "ext"));
        Assert.Throws<ArgumentNullException>(() => Misc.GetUniqueTempFileName("hint", null!));
        Assert.Throws<ArgumentException>(() => Misc.GetUniqueTempFileName("hint", string.Empty));
        Assert.Throws<ArgumentNullException>(
            () => Misc.GetUniqueTempFileName("hint", "ext", [null!]));
    }

    [Fact]
    public void PathChecksRequireFullyQualifiedPathsAndExpectedKinds()
    {
        using var directory = new TemporaryDirectory();
        string filePath = Path.Combine(directory.Path, "input.txt");
        string missingPath = Path.Combine(directory.Path, "missing");
        File.WriteAllText(filePath, "input");

        Assert.True(Misc.CheckDirectory(directory.Path, mustExist: true));
        Assert.True(Misc.CheckDirectory(missingPath, mustExist: false));
        Assert.False(Misc.CheckDirectory(filePath, mustExist: true));
        Assert.False(Misc.CheckDirectory("relative", mustExist: false));

        Assert.True(Misc.CheckFile(filePath, mustExist: true));
        Assert.True(Misc.CheckFile(missingPath, mustExist: false));
        Assert.False(Misc.CheckFile(directory.Path, mustExist: true));
        Assert.False(Misc.CheckFile("relative", mustExist: false));
    }

    [Fact]
    public void PathChecksRejectNull()
    {
        Assert.Throws<ArgumentNullException>(() => Misc.CheckDirectory(null!, mustExist: false));
        Assert.Throws<ArgumentNullException>(() => Misc.CheckFile(null!, mustExist: false));
    }

    [Fact]
    public void CommandLineArgumentQuotingEscapesBoundaryCases()
    {
        Assert.Equal("\"\"", Misc.QuoteCommandLineArgument(string.Empty));
        Assert.Equal("\"simple\"", Misc.QuoteCommandLineArgument("simple"));
        Assert.Equal("\"two words\"", Misc.QuoteCommandLineArgument("two words"));
        Assert.Equal("\"a\\\"b\"", Misc.QuoteCommandLineArgument("a\"b"));
        Assert.Equal("\"C:\\dir\\\\\"", Misc.QuoteCommandLineArgument("C:\\dir\\"));
    }

    [Fact]
    public void ConditionalArgumentQuotingCoversEmptyWhitespaceAndQuotes()
    {
        Assert.Equal("simple", Misc.QuoteCommandLineArgumentIfNeeded("simple"));
        Assert.Equal("\"\"", Misc.QuoteCommandLineArgumentIfNeeded(string.Empty));
        Assert.Equal("\"two words\"", Misc.QuoteCommandLineArgumentIfNeeded("two words"));
        Assert.Equal("\"a\\\"b\"", Misc.QuoteCommandLineArgumentIfNeeded("a\"b"));
        Assert.Equal(
            Misc.QuoteCommandLineArgument("C:\\directory with space\\"),
            Misc.QuoteCommandLinePath("C:\\directory with space\\"));
    }

    [Fact]
    public void CommandLineQuotingRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => Misc.QuoteCommandLineArgument(null!));
        Assert.Throws<ArgumentNullException>(() => Misc.QuoteCommandLineArgumentIfNeeded(null!));
        Assert.Throws<ArgumentNullException>(() => Misc.QuoteCommandLinePath(null!));
    }

    [Fact]
    public async Task ArgumentListPreservesEveryArgumentExactly()
    {
        string[] expectedArguments =
        [
            string.Empty,
            "plain",
            "two words",
            "quote\"value",
            "ends\\",
            "two\\\\slashes\\",
            "雪",
            "--name=value"
        ];

        ProcessResult result = await Misc.RunProcessAsync(
            ProcessTestHost.DotnetHostPath,
            ProcessTestHost.CreateArguments("arguments", expectedArguments),
            cancellationToken: TestContext.Current.CancellationToken);
        string[]? actualArguments = JsonSerializer.Deserialize<string[]>(result.StandardOutput);

        Assert.Equal(expectedArguments, actualArguments);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.IsSuccess);
        Assert.False(result.TimedOut);
        Assert.True(result.ProcessId > 0);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public void RawArgumentsRoundTripThroughTheQuotingHelpers()
    {
        string[] expectedArguments =
        [
            string.Empty,
            "two words",
            "quote\"value",
            "ends\\",
            "two\\\\slashes\\",
            "slashes\\\\\"quote",
            "雪"
        ];
        string[] helperArguments = ProcessTestHost.CreateArguments("arguments", expectedArguments);
        string rawArguments = string.Join(" ", helperArguments.Select(Misc.QuoteCommandLineArgument));

        ProcessResult result = Misc.RunProcessRaw(
            ProcessTestHost.DotnetHostPath,
            rawArguments,
            cancellationToken: TestContext.Current.CancellationToken);
        string[]? actualArguments = JsonSerializer.Deserialize<string[]>(result.StandardOutput);

        Assert.Equal(expectedArguments, actualArguments);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ProcessCapturesExactStreamsAndNonzeroExitCode()
    {
        string standardOutput = $"first{Environment.NewLine}第二";
        string standardError = $"warning{Environment.NewLine}错误";

        ProcessResult result = Misc.RunProcess(
            ProcessTestHost.DotnetHostPath,
            ProcessTestHost.CreateArguments("streams", standardOutput, standardError, "7"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(standardOutput, result.StandardOutput);
        Assert.Equal(standardError, result.StandardError);
        Assert.False(result.IsSuccess);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task ProcessOptionsApplyWorkingDirectoryAndEnvironmentChanges()
    {
        using var directory = new TemporaryDirectory();
        string assignedName = "INCANT_MISC_ASSIGNED_" + Guid.NewGuid().ToString("N");
        string removedName = "INCANT_MISC_REMOVED_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(removedName, "parent-value");

        try
        {
            var options = new ProcessOptions
            {
                WorkingDirectory = directory.Path,
                Environment = new Dictionary<string, string?>
                {
                    [assignedName] = "child-value",
                    [removedName] = null
                }
            };

            ProcessResult workingDirectoryResult = await Misc.RunProcessAsync(
                ProcessTestHost.DotnetHostPath,
                ProcessTestHost.CreateArguments("working-directory"),
                options,
                TestContext.Current.CancellationToken);
            ProcessResult assignedResult = await Misc.RunProcessAsync(
                ProcessTestHost.DotnetHostPath,
                ProcessTestHost.CreateArguments("environment", assignedName),
                options,
                TestContext.Current.CancellationToken);
            ProcessResult removedResult = await Misc.RunProcessAsync(
                ProcessTestHost.DotnetHostPath,
                ProcessTestHost.CreateArguments("environment", removedName),
                options,
                TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFullPath(directory.Path), Path.GetFullPath(workingDirectoryResult.StandardOutput));
            Assert.Equal("child-value", JsonSerializer.Deserialize<string>(assignedResult.StandardOutput));
            Assert.Equal("null", removedResult.StandardOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable(removedName, null);
        }
    }

    [Fact]
    public async Task ProcessOptionsDecodeStandardStreamsWithIndependentEncodings()
    {
        var options = new ProcessOptions
        {
            StandardOutputEncoding = Encoding.Latin1,
            StandardErrorEncoding = Encoding.Latin1
        };

        ProcessResult result = await Misc.RunProcessAsync(
            ProcessTestHost.DotnetHostPath,
            ProcessTestHost.CreateArguments("encoded-streams", Encoding.Latin1.WebName, "café", "façade"),
            options,
            TestContext.Current.CancellationToken);

        Assert.Equal("café", result.StandardOutput);
        Assert.Equal("façade", result.StandardError);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task TimeoutKillsTheProcessAndReturnsOutputProducedBeforeTermination()
    {
        var options = new ProcessOptions
        {
            Timeout = ProcessTestHost.ProcessTimeout
        };

        ProcessResult result = await Misc.RunProcessAsync(
            ProcessTestHost.DotnetHostPath,
            ProcessTestHost.CreateArguments("wait", "started", "waiting"),
            options,
            TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.False(result.IsSuccess);
        Assert.Equal("started", result.StandardOutput);
        Assert.Equal("waiting", result.StandardError);
        Assert.True(result.ProcessId > 0);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public void NonpositiveTimeoutIsRejectedBeforeExecution()
    {
        using var directory = new TemporaryDirectory();
        string markerPath = Path.Combine(directory.Path, "started.marker");
        var options = new ProcessOptions
        {
            Timeout = TimeSpan.Zero
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Misc.RunProcess(
                ProcessTestHost.DotnetHostPath,
                ProcessTestHost.CreateArguments("touch", markerPath),
                options,
                TestContext.Current.CancellationToken));
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task PrecanceledExecutionDoesNotStartTheProcess()
    {
        using var directory = new TemporaryDirectory();
        string markerPath = Path.Combine(directory.Path, "started.marker");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Misc.RunProcessAsync(
                ProcessTestHost.DotnetHostPath,
                ProcessTestHost.CreateArguments("touch", markerPath),
                cancellationToken: cancellation.Token));
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task CancellationTerminatesDescendantProcesses()
    {
        using var directory = new TemporaryDirectory();
        string childIdPath = Path.Combine(directory.Path, "child.pid");
        using var cancellation = new CancellationTokenSource();
        int childProcessId = 0;

        Task<ProcessResult> execution = Misc.RunProcessAsync(
            ProcessTestHost.DotnetHostPath,
            ProcessTestHost.CreateArguments(
                "spawn-child",
                childIdPath,
                ProcessTestHost.DotnetHostPath,
                ProcessTestHost.RuntimeConfigPath,
                ProcessTestHost.DepsFilePath),
            cancellationToken: cancellation.Token);

        try
        {
            await WaitForFileAsync(childIdPath, execution);
            childProcessId = int.Parse(File.ReadAllText(childIdPath), CultureInfo.InvariantCulture);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await execution);
            await WaitForProcessExitAsync(childProcessId);
        }
        finally
        {
            cancellation.Cancel();
            TryKillProcess(childProcessId);
        }
    }

    [Fact]
    public void UnixExecutablePermissionIsAddedByDefaultAndCanBeDisabled()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        string scriptPath = Path.Combine(directory.Path, "permission-test.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\nprintf permission-ok\n", new UTF8Encoding(false));
        UnixFileMode initialMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(scriptPath, initialMode);

        ProcessResult result = Misc.RunProcess(
            scriptPath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("permission-ok", result.StandardOutput);
        Assert.True(File.GetUnixFileMode(scriptPath).HasFlag(UnixFileMode.UserExecute));

        File.SetUnixFileMode(scriptPath, initialMode);
        var options = new ProcessOptions
        {
            EnsureUnixExecutablePermission = false
        };
        Assert.Throws<Win32Exception>(
            () => Misc.RunProcess(
                scriptPath,
                options: options,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ProcessEntryPointsRejectInvalidArguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => Misc.RunProcess(null!, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(
            () => Misc.RunProcess(string.Empty, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(
            () => Misc.RunProcess(
                ProcessTestHost.DotnetHostPath,
                [null!],
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(
            () => Misc.RunProcessRaw(
                ProcessTestHost.DotnetHostPath,
                null!,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async Task WaitForFileAsync(string path, Task<ProcessResult> execution)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < ProcessTestHost.AsynchronousTestTimeout)
        {
            if (File.Exists(path))
            {
                return;
            }

            if (execution.IsCompleted)
            {
                await execution;
                Assert.Fail("The process completed without creating the expected file.");
            }

            await Task.Delay(25);
        }

        Assert.Fail($"The file '{path}' was not created before the test timeout.");
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < ProcessTestHost.AsynchronousTestTimeout)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Process {processId} did not exit before the test timeout.");
    }

    private static void TryKillProcess(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}

[Collection(MiscDiagnosticsCollection.Name)]
public sealed class MiscDiagnosticsTests
{
    [Fact]
    public async Task ProcessExecutionWritesCorrelatedTraceEvents()
    {
        IncantTrace.Start(TraceCategory.Process);
        TraceCapture capture;
        try
        {
            ProcessResult result = await Misc.RunProcessAsync(
                ProcessTestHost.DotnetHostPath,
                ProcessTestHost.CreateArguments("exit", "0"),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.IsSuccess);
        }
        finally
        {
            capture = IncantTrace.Stop();
        }

        TraceEvent begin = Assert.Single(
            capture.Events.ToArray(),
            traceEvent => traceEvent.Kind == TraceEventKind.AsyncBegin);
        TraceEvent end = Assert.Single(
            capture.Events.ToArray(),
            traceEvent => traceEvent.Kind == TraceEventKind.AsyncEnd);
        Assert.Equal(TraceCategory.Process, begin.Category);
        Assert.Equal(ProcessTestHost.DotnetHostPath, begin.Name);
        Assert.NotEqual(0UL, begin.Id);
        Assert.Equal(begin.Id, end.Id);
        Assert.Equal(begin.Name, end.Name);
    }

    [Fact]
    public async Task TimeoutAndLaunchFailureWriteProcessErrorLogs()
    {
        Assert.False(IncantLog.IsRunning);
        Assert.Empty(IncantLog.Sinks);
        var sink = new CollectingLogSink();
        IncantLog.AddSink(sink);
        IncantLog.Start(new LogOptions());

        try
        {
            var options = new ProcessOptions
            {
                Timeout = ProcessTestHost.ProcessTimeout
            };
            ProcessResult timeoutResult = await Misc.RunProcessAsync(
                ProcessTestHost.DotnetHostPath,
                ProcessTestHost.CreateArguments("wait", string.Empty, string.Empty),
                options,
                TestContext.Current.CancellationToken);
            Assert.True(timeoutResult.TimedOut);

            string missingExecutable = Path.Combine(
                Path.GetTempPath(),
                "incant-missing-" + Guid.NewGuid().ToString("N"));
            await Assert.ThrowsAnyAsync<Exception>(
                async () => await Misc.RunProcessAsync(
                    missingExecutable,
                    cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            if (IncantLog.IsRunning)
            {
                IncantLog.Stop();
            }
            else if (IncantLog.Sinks.Contains(sink))
            {
                IncantLog.RemoveSink(sink);
            }
        }

        RenderedLogEvent timeoutEvent = Assert.Single(
            sink.Events,
            logEvent => logEvent.Message.Contains("timed out", StringComparison.Ordinal));
        RenderedLogEvent failureEvent = Assert.Single(
            sink.Events,
            logEvent => logEvent.Message.Contains("Failed to run process", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Error, timeoutEvent.Level);
        Assert.Equal(LogCategory.Process, timeoutEvent.Category);
        Assert.Null(timeoutEvent.ExceptionText);
        Assert.Equal(LogLevel.Error, failureEvent.Level);
        Assert.Equal(LogCategory.Process, failureEvent.Category);
        Assert.NotNull(failureEvent.ExceptionText);
    }

    private sealed class CollectingLogSink : ILogSink
    {
        public LogLevel MinimumLevel => LogLevel.Trace;

        public List<RenderedLogEvent> Events { get; } = [];

        public void Start(LogSinkContext context)
        {
        }

        public void Emit(RenderedLogEvent logEvent)
        {
            Events.Add(logEvent);
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MiscDiagnosticsCollection
{
    public const string Name = "Misc diagnostics";
}

internal static class ProcessTestHost
{
    public static string DotnetHostPath { get; } = FindDotnetHostPath();

    public static string RuntimeConfigPath { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "Incant.UnitTest.Base.runtimeconfig.json");

    public static string DepsFilePath { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "Incant.UnitTest.Base.deps.json");

    public static TimeSpan ProcessTimeout { get; } = TimeSpan.FromSeconds(2);

    public static TimeSpan AsynchronousTestTimeout { get; } = TimeSpan.FromSeconds(10);

    public static string[] CreateArguments(string command, params string[] arguments)
    {
        string helperAssemblyPath = Path.Combine(
            AppContext.BaseDirectory,
            "Incant.ProcessTestHelper.dll");
        return
        [
            "exec",
            "--runtimeconfig",
            RuntimeConfigPath,
            "--depsfile",
            DepsFilePath,
            helperAssemblyPath,
            command,
            .. arguments
        ];
    }

    private static string FindDotnetHostPath()
    {
        string? configuredPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(configuredPath) && File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        string executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (pathVariable is not null)
        {
            foreach (string directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(directory.Trim('"'), executableName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        throw new InvalidOperationException("The dotnet host could not be located for process tests.");
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Incant.UnitTest.Base",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
