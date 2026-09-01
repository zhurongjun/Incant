using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Incant.Base.Log;
using Incant.Base.Trace;
using IncantLog = Incant.Base.Log.Log;
using IncantTrace = Incant.Base.Trace.Trace;

namespace Incant.Base;

/// <summary>Configures one child-process execution.</summary>
public sealed class ProcessOptions
{
    /// <summary>
    /// Gets environment-variable changes applied to the inherited environment. A null value removes the variable.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Gets the child working directory, or null to inherit the current working directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the positive execution timeout, or null to wait indefinitely.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Gets the encoding used to decode standard output, or null to use the runtime default.</summary>
    public Encoding? StandardOutputEncoding { get; init; }

    /// <summary>Gets the encoding used to decode standard error, or null to use the runtime default.</summary>
    public Encoding? StandardErrorEncoding { get; init; }

    /// <summary>
    /// Gets a value indicating whether a directly specified Unix executable should receive user execute permission
    /// before it is started.
    /// </summary>
    public bool EnsureUnixExecutablePermission { get; init; } = true;
}

/// <summary>Describes a completed or timed-out child-process execution.</summary>
public sealed class ProcessResult
{
    internal ProcessResult(
        int? exitCode,
        string standardOutput,
        string standardError,
        bool timedOut,
        int processId,
        TimeSpan elapsed)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
        TimedOut = timedOut;
        ProcessId = processId;
        Elapsed = elapsed;
    }

    /// <summary>Gets the process exit code, or null when the process timed out.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets all decoded standard output.</summary>
    public string StandardOutput { get; }

    /// <summary>Gets all decoded standard error.</summary>
    public string StandardError { get; }

    /// <summary>Gets a value indicating whether the process exceeded its configured timeout.</summary>
    public bool TimedOut { get; }

    /// <summary>Gets a value indicating whether the process exited normally with code zero.</summary>
    public bool IsSuccess => !TimedOut && ExitCode == 0;

    /// <summary>Gets the operating-system process identifier.</summary>
    public int ProcessId { get; }

    /// <summary>Gets the monotonic elapsed time from successful launch until process exit.</summary>
    public TimeSpan Elapsed { get; }
}

/// <summary>Provides common process, command-line, path, and deterministic-name helpers.</summary>
public static class Misc
{
    /// <summary>Creates a deterministic file name from a hint and the MD5 of the arguments.</summary>
    /// <param name="hint">The readable file-name prefix.</param>
    /// <param name="extension">The extension written after the final period.</param>
    /// <param name="arguments">Optional arguments concatenated without separators to form the digest input.</param>
    /// <returns>A file name in the form <c>&lt;Hint&gt;_&lt;MD5&gt;.&lt;Extension&gt;</c>.</returns>
    /// <exception cref="ArgumentNullException">A required string or an argument element is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="hint"/> or <paramref name="extension"/> is empty.</exception>
    /// <remarks>The MD5 digest is a compatibility identifier and is not used for security.</remarks>
    public static string GetUniqueTempFileName(
        string hint,
        string extension,
        IEnumerable<string>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(hint);
        ArgumentException.ThrowIfNullOrEmpty(extension);

        var identifier = new StringBuilder();
        if (arguments is not null)
        {
            foreach (string? argument in arguments)
            {
                if (argument is null)
                {
                    throw new ArgumentNullException(nameof(arguments), "Arguments cannot contain null values.");
                }

                identifier.Append(argument);
            }
        }

        byte[] digest = MD5.HashData(Encoding.UTF8.GetBytes(identifier.ToString()));
        return $"{hint}_{Convert.ToHexString(digest)}.{extension}";
    }

    /// <summary>Checks whether a path is fully qualified and optionally names an existing directory.</summary>
    /// <param name="path">The directory path to inspect.</param>
    /// <param name="mustExist">Whether the directory must exist.</param>
    /// <returns>True when the path satisfies the requested checks; otherwise, false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static bool CheckDirectory(string path, bool mustExist)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Path.IsPathFullyQualified(path) && (!mustExist || Directory.Exists(path));
    }

    /// <summary>Checks whether a path is fully qualified and optionally names an existing file.</summary>
    /// <param name="path">The file path to inspect.</param>
    /// <param name="mustExist">Whether the file must exist.</param>
    /// <returns>True when the path satisfies the requested checks; otherwise, false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static bool CheckFile(string path, bool mustExist)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Path.IsPathFullyQualified(path) && (!mustExist || File.Exists(path));
    }

    /// <summary>Quotes one argument using the command-line rules used by .NET argument-list execution.</summary>
    /// <param name="argument">The unquoted argument.</param>
    /// <returns>The argument surrounded by quotes with embedded quotes and backslashes escaped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="argument"/> is null.</exception>
    /// <remarks>This method does not perform shell escaping.</remarks>
    public static string QuoteCommandLineArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');

        int backslashCount = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashCount);
                builder.Append(character);
            }

            backslashCount = 0;
        }

        builder.Append('\\', backslashCount * 2);
        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>Quotes a path as one command-line argument.</summary>
    /// <param name="path">The unquoted path.</param>
    /// <returns>The quoted path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static string QuoteCommandLinePath(string path) => QuoteCommandLineArgument(path);

    /// <summary>Quotes an argument only when its contents require quoting.</summary>
    /// <param name="argument">The unquoted argument.</param>
    /// <returns>The original argument or its quoted representation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="argument"/> is null.</exception>
    /// <remarks>Empty arguments, whitespace, and embedded double quotes require quoting.</remarks>
    public static string QuoteCommandLineArgumentIfNeeded(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        return argument.Length == 0
            || argument.Any(char.IsWhiteSpace)
            || argument.Contains('"')
                ? QuoteCommandLineArgument(argument)
                : argument;
    }

    /// <summary>Runs a process synchronously with an unescaped argument list.</summary>
    /// <param name="executablePath">The executable path or name.</param>
    /// <param name="arguments">Unescaped argument values, or null for no arguments.</param>
    /// <param name="options">Optional process settings.</param>
    /// <param name="cancellationToken">A token that terminates the process tree and cancels the operation.</param>
    /// <returns>The completed process result.</returns>
    /// <exception cref="ArgumentNullException">The executable path or an argument element is null.</exception>
    /// <exception cref="ArgumentException">The executable path is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static ProcessResult RunProcess(
        string executablePath,
        IReadOnlyList<string>? arguments = null,
        ProcessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return RunProcessAsync(executablePath, arguments, options, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>Runs a process asynchronously with an unescaped argument list.</summary>
    /// <param name="executablePath">The executable path or name.</param>
    /// <param name="arguments">Unescaped argument values, or null for no arguments.</param>
    /// <param name="options">Optional process settings.</param>
    /// <param name="cancellationToken">A token that terminates the process tree and cancels the operation.</param>
    /// <returns>A task containing the completed process result.</returns>
    /// <exception cref="ArgumentNullException">The executable path or an argument element is null.</exception>
    /// <exception cref="ArgumentException">The executable path is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static Task<ProcessResult> RunProcessAsync(
        string executablePath,
        IReadOnlyList<string>? arguments = null,
        ProcessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateExecutablePath(executablePath);
        ProcessOptions effectiveOptions = ValidateOptions(options);
        ProcessStartInfo startInfo = CreateStartInfo(executablePath, effectiveOptions);
        AddArguments(startInfo, arguments);
        string displayedArguments = FormatArguments(arguments);
        return RunProcessCoreAsync(
            startInfo,
            displayedArguments,
            effectiveOptions.Timeout,
            effectiveOptions.EnsureUnixExecutablePermission,
            cancellationToken);
    }

    /// <summary>Runs a process synchronously with an already composed raw argument string.</summary>
    /// <param name="executablePath">The executable path or name.</param>
    /// <param name="arguments">The already quoted raw argument string.</param>
    /// <param name="options">Optional process settings.</param>
    /// <param name="cancellationToken">A token that terminates the process tree and cancels the operation.</param>
    /// <returns>The completed process result.</returns>
    /// <exception cref="ArgumentNullException">The executable path or raw argument string is null.</exception>
    /// <exception cref="ArgumentException">The executable path is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    /// <remarks>The raw argument string is passed directly to .NET and is not interpreted by a shell.</remarks>
    public static ProcessResult RunProcessRaw(
        string executablePath,
        string arguments,
        ProcessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return RunProcessRawAsync(executablePath, arguments, options, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>Runs a process asynchronously with an already composed raw argument string.</summary>
    /// <param name="executablePath">The executable path or name.</param>
    /// <param name="arguments">The already quoted raw argument string.</param>
    /// <param name="options">Optional process settings.</param>
    /// <param name="cancellationToken">A token that terminates the process tree and cancels the operation.</param>
    /// <returns>A task containing the completed process result.</returns>
    /// <exception cref="ArgumentNullException">The executable path or raw argument string is null.</exception>
    /// <exception cref="ArgumentException">The executable path is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    /// <remarks>The raw argument string is passed directly to .NET and is not interpreted by a shell.</remarks>
    public static Task<ProcessResult> RunProcessRawAsync(
        string executablePath,
        string arguments,
        ProcessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateExecutablePath(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ProcessOptions effectiveOptions = ValidateOptions(options);
        ProcessStartInfo startInfo = CreateStartInfo(executablePath, effectiveOptions);
        startInfo.Arguments = arguments;
        return RunProcessCoreAsync(
            startInfo,
            arguments,
            effectiveOptions.Timeout,
            effectiveOptions.EnsureUnixExecutablePermission,
            cancellationToken);
    }

    private static async Task<ProcessResult> RunProcessCoreAsync(
        ProcessStartInfo startInfo,
        string displayedArguments,
        TimeSpan? timeout,
        bool ensureUnixExecutablePermission,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool traceStarted = IncantTrace.IsEnabled(TraceCategory.Process);
        ulong traceId = 0;
        if (traceStarted)
        {
            traceId = IncantTrace.CreateId();
            IncantTrace.AsyncBegin(TraceCategory.Process, startInfo.FileName, traceId);
        }

        try
        {
            EnsureUnixExecutablePermission(startInfo.FileName, ensureUnixExecutablePermission);

            using var process = new Process
            {
                StartInfo = startInfo
            };
            if (!process.Start())
            {
                throw new InvalidOperationException($"The process '{startInfo.FileName}' could not be started.");
            }

            int processId = process.Id;
            long startTimestamp = Stopwatch.GetTimestamp();
            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
            Task waitTask = process.WaitForExitAsync();
            TimeSpan timeoutValue = timeout.GetValueOrDefault();

            try
            {
                if (timeout is null)
                {
                    await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await waitTask.WaitAsync(timeoutValue, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
                await TerminateAndWaitAsync(process, waitTask).ConfigureAwait(false);
                long endTimestamp = Stopwatch.GetTimestamp();
                (string standardOutput, string standardError) = await ReadOutputAsync(
                    standardOutputTask,
                    standardErrorTask).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                IncantLog.Error(
                    LogCategory.Process,
                    "Process {ExecutablePath} with arguments {Arguments} timed out after {Timeout}.",
                    startInfo.FileName,
                    displayedArguments,
                    timeoutValue);
                return new ProcessResult(
                    null,
                    standardOutput,
                    standardError,
                    true,
                    processId,
                    Stopwatch.GetElapsedTime(startTimestamp, endTimestamp));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await TerminateAndWaitAsync(process, waitTask).ConfigureAwait(false);
                await ReadOutputAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
                throw;
            }

            long completedTimestamp = Stopwatch.GetTimestamp();
            (string completedOutput, string completedError) = await ReadOutputAsync(
                standardOutputTask,
                standardErrorTask).ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                completedOutput,
                completedError,
                false,
                processId,
                Stopwatch.GetElapsedTime(startTimestamp, completedTimestamp));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            IncantLog.Error(
                LogCategory.Process,
                exception,
                "Failed to run process {ExecutablePath} with arguments {Arguments}.",
                startInfo.FileName,
                displayedArguments);
            throw;
        }
        finally
        {
            if (traceStarted)
            {
                IncantTrace.AsyncEnd(TraceCategory.Process, startInfo.FileName, traceId);
            }
        }
    }

    private static ProcessOptions ValidateOptions(ProcessOptions? options)
    {
        ProcessOptions effectiveOptions = options ?? new ProcessOptions();
        if (effectiveOptions.Timeout is TimeSpan timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                effectiveOptions.Timeout,
                "The process timeout must be positive.");
        }

        return effectiveOptions;
    }

    private static void ValidateExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath, ProcessOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (options.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = options.WorkingDirectory;
        }

        if (options.StandardOutputEncoding is not null)
        {
            startInfo.StandardOutputEncoding = options.StandardOutputEncoding;
        }

        if (options.StandardErrorEncoding is not null)
        {
            startInfo.StandardErrorEncoding = options.StandardErrorEncoding;
        }

        if (options.Environment is not null)
        {
            foreach ((string name, string? value) in options.Environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        return startInfo;
    }

    private static void AddArguments(ProcessStartInfo startInfo, IReadOnlyList<string>? arguments)
    {
        if (arguments is null)
        {
            return;
        }

        for (int index = 0; index < arguments.Count; ++index)
        {
            string? argument = arguments[index];
            if (argument is null)
            {
                throw new ArgumentNullException(nameof(arguments), $"Argument at index {index} is null.");
            }

            startInfo.ArgumentList.Add(argument);
        }
    }

    private static string FormatArguments(IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (int index = 0; index < arguments.Count; ++index)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(QuoteCommandLineArgumentIfNeeded(arguments[index]));
        }

        return builder.ToString();
    }

    private static void EnsureUnixExecutablePermission(string executablePath, bool isEnabled)
    {
        if (!isEnabled || OperatingSystem.IsWindows() || !File.Exists(executablePath))
        {
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(executablePath);
        if (!mode.HasFlag(UnixFileMode.UserExecute))
        {
            File.SetUnixFileMode(executablePath, mode | UnixFileMode.UserExecute);
        }
    }

    private static async Task TerminateAndWaitAsync(Process process, Task waitTask)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }

        await waitTask.ConfigureAwait(false);
    }

    private static async Task<(string StandardOutput, string StandardError)> ReadOutputAsync(
        Task<string> standardOutputTask,
        Task<string> standardErrorTask)
    {
        await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
        string standardOutput = await standardOutputTask.ConfigureAwait(false);
        string standardError = await standardErrorTask.ConfigureAwait(false);
        return (standardOutput, standardError);
    }
}
