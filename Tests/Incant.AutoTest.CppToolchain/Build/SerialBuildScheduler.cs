using System.Diagnostics;
using Incant.Base;

namespace Incant.AutoTest.CppToolchain;

internal sealed class SerialBuildScheduler
{
    internal async Task<bool> RunAsync(
        AutoTestContext context,
        ToolchainCandidate candidate,
        BuildActionPhase phase,
        CancellationToken cancellationToken)
    {
        BuildPlan plan = candidate.BuildPlan
            ?? throw new InvalidOperationException("The candidate has no build plan.");
        if (phase == BuildActionPhase.Build)
        {
            _ = AutoTestWorkspace.ResetLogDirectory(context, candidate.Id);
        }

        foreach (BuildAction action in plan.Actions.Where(action => action.Phase == phase))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                candidate.Status = CandidateStatus.Canceled;
                cancellationToken.ThrowIfCancellationRequested();
            }
            BuildActionResult[] dependencies = action.Dependencies
                .Select(dependency => candidate.Actions
                    .SingleOrDefault(result => result.Id == dependency))
                .OfType<BuildActionResult>()
                .ToArray();
            if (dependencies.Length != action.Dependencies.Count
                || dependencies.Any(result => result.Status != BuildActionStatus.Passed))
            {
                candidate.Actions.Add(new BuildActionResult
                {
                    Id = action.Id,
                    Phase = action.Phase,
                    ExecutablePath = action.ExecutablePath,
                    Arguments = action.Arguments,
                    Status = BuildActionStatus.Skipped,
                    Error = "A dependency did not pass.",
                });
                continue;
            }

            BuildActionResult result = await RunActionAsync(
                context, candidate, action, cancellationToken).ConfigureAwait(false);
            candidate.Actions.Add(result);
            if (result.Status == BuildActionStatus.Canceled)
            {
                candidate.Status = CandidateStatus.Canceled;
                throw new OperationCanceledException(cancellationToken);
            }
        }

        bool succeeded = candidate.Actions
            .Where(result => result.Phase == phase)
            .All(result => result.Status == BuildActionStatus.Passed);
        if (!succeeded)
        {
            candidate.Status = phase == BuildActionPhase.Build
                ? CandidateStatus.BuildFailed
                : CandidateStatus.ExecutionFailed;
            candidate.Failures.Add(
                phase == BuildActionPhase.Build
                    ? "One or more build actions failed."
                    : "One or more execution actions failed.");
        }

        return succeeded;
    }

    internal static void DeleteSuccessfulWork(
        AutoTestContext context,
        ToolchainCandidate candidate)
    {
        if (context.Options.KeepWork
            || candidate.Status != CandidateStatus.Passed
            || candidate.BuildPlan is null
            || !Directory.Exists(candidate.BuildPlan.WorkDirectory))
        {
            return;
        }

        try
        {
            AutoTestWorkspace.DeleteDirectory(
                context.Options.WorkRoot,
                candidate.BuildPlan.WorkDirectory);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            candidate.Decisions.Add(
                $"Successful work directory could not be removed: {exception.Message}");
        }
    }

    private static async Task<BuildActionResult> RunActionAsync(
        AutoTestContext context,
        ToolchainCandidate candidate,
        BuildAction action,
        CancellationToken cancellationToken)
    {
        var result = new BuildActionResult
        {
            Id = action.Id,
            Phase = action.Phase,
            ExecutablePath = action.ExecutablePath,
            Arguments = action.Arguments,
        };
        string logDirectory = Path.Combine(
            context.Options.WorkRoot, "logs", candidate.Id);
        string stdoutPath = Path.Combine(logDirectory, action.Id + ".stdout.log");
        string stderrPath = Path.Combine(logDirectory, action.Id + ".stderr.log");
        string standardOutput = string.Empty;
        string standardError = string.Empty;
        long started = Stopwatch.GetTimestamp();
        try
        {
            Directory.CreateDirectory(logDirectory);
            Directory.CreateDirectory(action.WorkingDirectory);
            (string executable, IReadOnlyList<string> arguments) =
                ResolveWrapper(context, candidate, action);
            result.ExecutablePath = executable;
            result.Arguments = arguments;
            ProcessResult process = await Misc.RunProcessAsync(
                executable,
                arguments,
                new ProcessOptions
                {
                    WorkingDirectory = action.WorkingDirectory,
                    Environment = action.Environment,
                    Timeout = action.Timeout,
                },
                cancellationToken).ConfigureAwait(false);
            standardOutput = process.StandardOutput;
            standardError = process.StandardError;
            result.ExitCode = process.ExitCode;
            result.TimedOut = process.TimedOut;
            result.Elapsed = process.Elapsed;
            result.Artifacts = action.ExpectedArtifacts
                .Where(File.Exists)
                .Select(path => Path.GetRelativePath(
                    context.Options.WorkRoot, path))
                .ToArray();

            string combinedOutput = process.StandardOutput + process.StandardError;
            string? missingOutput = action.ExpectedOutputFragments
                .FirstOrDefault(fragment => !combinedOutput.Contains(
                    fragment,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal));
            string? invalidArtifact = action.ExpectedArtifacts.FirstOrDefault(
                path => !File.Exists(path) || new FileInfo(path).Length == 0);
            if (!process.IsSuccess)
            {
                result.Error = process.TimedOut
                    ? $"Process timed out after {action.Timeout}."
                    : $"Process exited with code {process.ExitCode}.";
            }
            else if (invalidArtifact is not null)
            {
                result.Error =
                    $"Expected artifact '{invalidArtifact}' is absent or empty.";
            }
            else if (missingOutput is not null)
            {
                result.Error =
                    $"Process output does not contain '{missingOutput}'.";
            }
            else
            {
                result.Status = BuildActionStatus.Passed;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            standardError += "The process was canceled.";
            result.Error = "The process was canceled.";
            result.Status = BuildActionStatus.Canceled;
        }
        catch (Exception exception)
        {
            standardError += exception;
            result.Error = exception.Message;
        }
        finally
        {
            if (result.Elapsed == TimeSpan.Zero)
            {
                result.Elapsed = Stopwatch.GetElapsedTime(started);
            }

            if (result.Status == BuildActionStatus.Pending)
            {
                result.Status = BuildActionStatus.Failed;
            }

            string? stdoutError = await TryWriteLogAsync(
                stdoutPath, standardOutput).ConfigureAwait(false);
            if (stdoutError is null)
            {
                result.StandardOutputLog = Path.GetRelativePath(
                    context.Options.WorkRoot, stdoutPath);
            }
            else
            {
                RecordLogFailure(result, "stdout", stdoutError);
            }

            string? stderrError = await TryWriteLogAsync(
                stderrPath, standardError).ConfigureAwait(false);
            if (stderrError is null)
            {
                result.StandardErrorLog = Path.GetRelativePath(
                    context.Options.WorkRoot, stderrPath);
            }
            else
            {
                RecordLogFailure(result, "stderr", stderrError);
            }
        }

        return result;
    }

    private static async Task<string?> TryWriteLogAsync(
        string path,
        string content)
    {
        try
        {
            await File.WriteAllTextAsync(
                path, content, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return exception.Message;
        }
    }

    private static void RecordLogFailure(
        BuildActionResult result,
        string stream,
        string error)
    {
        string message = $"The {stream} log could not be written: {error}";
        result.Error = string.IsNullOrWhiteSpace(result.Error)
            ? message
            : result.Error + Environment.NewLine + message;
        if (result.Status == BuildActionStatus.Passed)
        {
            result.Status = BuildActionStatus.Failed;
        }
    }

    private static (string Executable, IReadOnlyList<string> Arguments) ResolveWrapper(
        AutoTestContext context,
        ToolchainCandidate candidate,
        BuildAction action)
    {
        string extension = Path.GetExtension(action.ExecutablePath);
        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            return (FindPython(context, candidate), new[]
            {
                action.ExecutablePath,
            }.Concat(action.Arguments).ToArray());
        }

        if (OperatingSystem.IsWindows()
            && (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)))
        {
            string pythonWrapper = Path.ChangeExtension(
                action.ExecutablePath, ".py");
            if (!File.Exists(pythonWrapper))
            {
                throw new InvalidOperationException(
                    $"Wrapper '{action.ExecutablePath}' has no direct Python entry point.");
            }

            return (FindPython(context, candidate), new[]
            {
                pythonWrapper,
            }.Concat(action.Arguments).ToArray());
        }

        return (action.ExecutablePath, action.Arguments);
    }

    private static string FindPython(
        AutoTestContext context,
        ToolchainCandidate candidate)
    {
        ResolvedToolchain toolchain = candidate.Toolchain!;
        if (toolchain.Environment.GetValueOrDefault("EMSDK_PYTHON") is string configured
            && File.Exists(configured))
        {
            return configured;
        }

        RuntimeManifest? runtime = context.Manifest!.Runtimes.FirstOrDefault(
            item => item.Kind == RuntimeKind.Python
                && item.InstallationId is not null
                && candidate.InstallationIds.Contains(
                    item.InstallationId, StringComparer.Ordinal));
        if (runtime is not null)
        {
            return runtime.Path;
        }

        string pathValue = toolchain.Environment.GetValueOrDefault("PATH")
            ?? System.Environment.GetEnvironmentVariable("PATH")
            ?? string.Empty;
        string[] extensions = OperatingSystem.IsWindows()
            ? [".exe", ".cmd", ".bat", string.Empty]
            : [string.Empty];
        foreach (string directory in pathValue.Split(
            Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in new[] { "python", "python3" })
            {
                foreach (string candidateExtension in extensions)
                {
                    string path = Path.Combine(
                        directory.Trim().Trim('"'),
                        name + candidateExtension);
                    if (File.Exists(path)
                        && !path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                        && !path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }
                }
            }
        }

        throw new InvalidOperationException(
            "A direct Python executable is required to run this wrapper.");
    }
}
