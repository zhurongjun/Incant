using Incant.Base;

namespace Incant.AutoTest.CXLegacyToolchain.Setup;

internal sealed class SetupCommandRunner(SetupContext context)
{
    private const int OutputTailLimit = 32 * 1024;
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan s_heartbeatInterval = TimeSpan.FromSeconds(30);

    internal async Task<SetupCommandOutput> RunAsync(
        string file,
        IReadOnlyList<string> arguments,
        SetupCommandOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        ArgumentNullException.ThrowIfNull(arguments);

        SetupCommandOptions effectiveOptions = options ?? new SetupCommandOptions();
        if (effectiveOptions.Attempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                effectiveOptions.Attempts,
                "Command attempts must be positive.");
        }

        if (effectiveOptions.RetryDelay is TimeSpan retryDelay && retryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                effectiveOptions.RetryDelay,
                "The command retry delay cannot be negative.");
        }

        SetupCommandException? lastError = null;
        for (int attempt = 1; attempt <= effectiveOptions.Attempts; ++attempt)
        {
            try
            {
                return await RunOnceAsync(
                    file, arguments, effectiveOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (SetupCommandException exception) when (attempt < effectiveOptions.Attempts)
            {
                lastError = exception;
                TimeSpan baseDelay = effectiveOptions.RetryDelay ?? TimeSpan.FromSeconds(2);
                TimeSpan delay = TimeSpan.FromTicks(baseDelay.Ticks * attempt);
                Console.Error.WriteLine(
                    $"[command:retry] attempt={attempt} nextAttempt={attempt + 1} "
                    + $"delayMs={delay.TotalMilliseconds:F0} reason={exception.Message}");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        if (lastError is not null)
        {
            throw lastError;
        }

        throw new InvalidOperationException("The command retry loop ended unexpectedly.");
    }

    internal Task<SetupCommandOutput> RunAsync(
        string file,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        RunAsync(file, arguments, null, cancellationToken);

    private async Task<SetupCommandOutput> RunOnceAsync(
        string file,
        IReadOnlyList<string> arguments,
        SetupCommandOptions options,
        CancellationToken cancellationToken)
    {
        int sequence = context.NextCommandSequence;
        string workingDirectory = Path.GetFullPath(
            options.WorkingDirectory ?? context.Options.Workspace);
        string logDirectory = Path.Combine(context.Options.WorkRoot, "logs");
        Directory.CreateDirectory(logDirectory);
        string stem = $"{sequence:D4}-{Sanitize(Path.GetFileNameWithoutExtension(file))}";
        string standardOutputPath = Path.Combine(logDirectory, stem + ".stdout.log");
        string standardErrorPath = Path.Combine(logDirectory, stem + ".stderr.log");
        var record = new SetupCommandRecord
        {
            Sequence = sequence,
            File = file,
            Arguments = arguments.ToArray(),
            WorkingDirectory = workingDirectory,
            Stage = context.ActiveStage,
            ComponentId = context.ActiveComponentId,
            Environment = options.Environment is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?>(options.Environment),
            StartedAt = DateTimeOffset.UtcNow,
            StandardOutputLog = RelativeLogPath(standardOutputPath),
            StandardErrorLog = RelativeLogPath(standardErrorPath),
        };
        context.AddCommand(record);

        string display = FormatCommand(file, arguments);
        Console.WriteLine($"[command:start] {display}");
        Console.WriteLine($"[command:cwd] {workingDirectory}");

        Task<ProcessResult> processTask;
        try
        {
            processTask = Misc.RunProcessAsync(
                file,
                arguments,
                new ProcessOptions
                {
                    WorkingDirectory = workingDirectory,
                    Environment = options.Environment,
                    Timeout = options.Timeout ?? s_defaultTimeout,
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            await WriteLogsAsync(
                standardOutputPath, standardErrorPath, string.Empty, string.Empty)
                .ConfigureAwait(false);
            record.Status = SetupResultStatus.Failed;
            record.CompletedAt = DateTimeOffset.UtcNow;
            record.Elapsed = record.CompletedAt.Value - record.StartedAt;
            record.Error = SetupErrorRecord.FromException(exception);
            throw new SetupCommandException(
                $"Could not start command: {display}",
                record,
                exception);
        }

        while (!processTask.IsCompleted)
        {
            Task delay = Task.Delay(s_heartbeatInterval, cancellationToken);
            Task completed = await Task.WhenAny(processTask, delay).ConfigureAwait(false);
            if (completed == processTask)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await processTask.ConfigureAwait(false);
            }

            Console.WriteLine(
                $"[command:alive] sequence={sequence} elapsed={DateTimeOffset.UtcNow - record.StartedAt:c}");
        }

        ProcessResult result;
        try
        {
            result = await processTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteLogsAsync(
                standardOutputPath, standardErrorPath, string.Empty, string.Empty)
                .ConfigureAwait(false);
            record.Status = SetupResultStatus.Cancelled;
            record.CompletedAt = DateTimeOffset.UtcNow;
            record.Elapsed = record.CompletedAt.Value - record.StartedAt;
            throw;
        }
        catch (Exception exception)
        {
            await WriteLogsAsync(
                standardOutputPath, standardErrorPath, string.Empty, string.Empty)
                .ConfigureAwait(false);
            record.Status = SetupResultStatus.Failed;
            record.CompletedAt = DateTimeOffset.UtcNow;
            record.Elapsed = record.CompletedAt.Value - record.StartedAt;
            record.Error = SetupErrorRecord.FromException(exception);
            throw new SetupCommandException(
                $"Could not run command: {display}",
                record,
                exception);
        }

        await WriteLogsAsync(
            standardOutputPath,
            standardErrorPath,
            result.StandardOutput,
            result.StandardError).ConfigureAwait(false);
        record.ExitCode = result.ExitCode;
        record.TimedOut = result.TimedOut;
        record.CompletedAt = DateTimeOffset.UtcNow;
        record.Elapsed = result.Elapsed;
        record.StandardOutputTail = Tail(result.StandardOutput);
        record.StandardErrorTail = Tail(result.StandardError);

        if (result.StandardOutput.Length > 0)
        {
            Console.Write(result.StandardOutput);
        }

        if (result.StandardError.Length > 0)
        {
            Console.Error.Write(result.StandardError);
        }

        if (!result.IsSuccess)
        {
            record.Status = SetupResultStatus.Failed;
            string reason = result.TimedOut
                ? $"Command timed out after {result.Elapsed}."
                : $"Command exited with code {result.ExitCode}.";
            var exception = new SetupCommandException(
                $"{reason} Command: {display}",
                record);
            record.Error = SetupErrorRecord.FromException(exception);
            Console.Error.WriteLine(
                $"[command:failure] sequence={sequence} exit={result.ExitCode?.ToString() ?? "none"} "
                + $"timedOut={result.TimedOut} durationMs={result.Elapsed.TotalMilliseconds:F0}");
            throw exception;
        }

        record.Status = SetupResultStatus.Succeeded;
        Console.WriteLine(
            $"[command:success] sequence={sequence} exit=0 durationMs={result.Elapsed.TotalMilliseconds:F0}");
        return new SetupCommandOutput(
            result.StandardOutput,
            result.StandardError,
            result.ExitCode.GetValueOrDefault(),
            result.Elapsed);
    }

    private static async Task WriteLogsAsync(
        string standardOutputPath,
        string standardErrorPath,
        string standardOutput,
        string standardError)
    {
        await Task.WhenAll(
            File.WriteAllTextAsync(standardOutputPath, standardOutput, CancellationToken.None),
            File.WriteAllTextAsync(standardErrorPath, standardError, CancellationToken.None))
            .ConfigureAwait(false);
    }

    private string RelativeLogPath(string path) =>
        Path.GetRelativePath(context.Options.WorkRoot, path)
            .Replace(Path.DirectorySeparatorChar, '/');

    private static string Tail(string value) =>
        value.Length <= OutputTailLimit ? value : value[^OutputTailLimit..];

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static string FormatCommand(string file, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { file }.Concat(arguments)
            .Select(Misc.QuoteCommandLineArgumentIfNeeded));
}
