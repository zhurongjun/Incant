namespace Incant.AutoTest.CXLegacyToolchain.Setup;

internal enum SetupResultStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
}

internal sealed class SetupErrorRecord
{
    internal required string Type { get; init; }

    internal required string Message { get; init; }

    internal required string? StackTrace { get; init; }

    internal required SetupErrorRecord? InnerError { get; init; }

    internal static SetupErrorRecord FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new SetupErrorRecord
        {
            Type = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            StackTrace = exception.StackTrace,
            InnerError = exception.InnerException is null
                ? null
                : FromException(exception.InnerException),
        };
    }
}

internal sealed class SetupStageRecord
{
    internal required string Name { get; init; }

    internal SetupResultStatus Status { get; set; } = SetupResultStatus.Pending;

    internal DateTimeOffset? StartedAt { get; set; }

    internal DateTimeOffset? CompletedAt { get; set; }

    internal TimeSpan Elapsed { get; set; }

    internal string? SkipReason { get; set; }

    internal SetupErrorRecord? Error { get; set; }
}

internal sealed class SetupComponentRecord
{
    internal required string Id { get; init; }

    internal required string Name { get; init; }

    internal required string Stage { get; init; }

    internal required IReadOnlyList<string> Dependencies { get; init; }

    internal SetupResultStatus Status { get; set; } = SetupResultStatus.Pending;

    internal DateTimeOffset? StartedAt { get; set; }

    internal DateTimeOffset? CompletedAt { get; set; }

    internal TimeSpan Elapsed { get; set; }

    internal string? SkipReason { get; set; }

    internal SetupErrorRecord? Error { get; set; }
}

internal sealed class SetupCommandRecord
{
    internal required int Sequence { get; init; }

    internal required string File { get; init; }

    internal required IReadOnlyList<string> Arguments { get; init; }

    internal required string WorkingDirectory { get; init; }

    internal required string? Stage { get; init; }

    internal required string? ComponentId { get; init; }

    internal required IReadOnlyDictionary<string, string?> Environment { get; init; }

    internal SetupResultStatus Status { get; set; } = SetupResultStatus.Running;

    internal int? ExitCode { get; set; }

    internal bool TimedOut { get; set; }

    internal DateTimeOffset StartedAt { get; init; }

    internal DateTimeOffset? CompletedAt { get; set; }

    internal TimeSpan Elapsed { get; set; }

    internal required string StandardOutputLog { get; init; }

    internal required string StandardErrorLog { get; init; }

    internal string? StandardOutputTail { get; set; }

    internal string? StandardErrorTail { get; set; }

    internal SetupErrorRecord? Error { get; set; }
}

internal sealed record SetupCommandOptions(
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null,
    int Attempts = 1,
    TimeSpan? RetryDelay = null);

internal sealed record SetupCommandOutput(
    string StandardOutput,
    string StandardError,
    int ExitCode,
    TimeSpan Elapsed);

internal sealed class SetupCommandException(
    string message,
    SetupCommandRecord command,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    internal SetupCommandRecord Command { get; } = command;
}

internal sealed record ProvisioningResult(
    IReadOnlyList<InstallationManifest> Installations,
    IReadOnlyList<RuntimeManifest> Runtimes)
{
    internal static ProvisioningResult Empty { get; } = new([], []);

    internal static ProvisioningResult ForInstallation(InstallationManifest installation) =>
        new([installation], []);

    internal static ProvisioningResult ForRuntime(RuntimeManifest runtime) =>
        new([], [runtime]);
}

internal interface ISetupComponent
{
    string Id { get; }

    string Name { get; }

    IReadOnlyList<string> Dependencies { get; }

    Task<ProvisioningResult> ProvisionAsync(
        SetupContext context,
        CancellationToken cancellationToken);
}
