using Incant.CXLegacy.Arguments;

namespace Incant.AutoTest.CXLegacyToolchain;

internal enum BuildActionPhase
{
    Build,
    Execute,
}

internal enum BuildActionStatus
{
    Pending,
    Passed,
    Failed,
    Skipped,
    Canceled,
}

internal sealed record BuildAction(
    string Id,
    BuildActionPhase Phase,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> ExpectedArtifacts,
    IReadOnlyList<string> ExpectedOutputFragments,
    TimeSpan Timeout)
{
    internal ResponseFileDialect? ResponseDialect { get; init; }

    internal int ResponseArgumentOffset { get; init; }

    internal IReadOnlyList<string> RecreatedArtifacts { get; init; } = [];
}

internal sealed class BuildPlan
{
    internal required string WorkDirectory { get; init; }

    internal required IReadOnlyList<BuildAction> Actions { get; init; }
}

internal sealed class BuildActionResult
{
    internal required string Id { get; init; }

    internal required BuildActionPhase Phase { get; init; }

    internal required string ExecutablePath { get; set; }

    internal required IReadOnlyList<string> Arguments { get; set; }

    internal BuildActionStatus Status { get; set; } = BuildActionStatus.Pending;

    internal IReadOnlyList<string>? TransportArguments { get; set; }

    internal int? ExitCode { get; set; }

    internal bool TimedOut { get; set; }

    internal TimeSpan Elapsed { get; set; }

    internal string? StandardOutputLog { get; set; }

    internal string? StandardErrorLog { get; set; }

    internal IReadOnlyList<string> Artifacts { get; set; } = [];

    internal string? Error { get; set; }
}
