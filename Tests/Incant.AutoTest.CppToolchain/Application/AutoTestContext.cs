using System.Collections;
using Incant.Core.Cpp;

namespace Incant.AutoTest.CppToolchain;

internal sealed class AutoTestContext(AutoTestOptions options)
{
    private readonly HashSet<Diagnostic> _diagnostics = [];

    internal AutoTestOptions Options { get; } = options;

    internal EnvironmentProfile Profile => Options.Profile;

    internal DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    internal DateTimeOffset? CompletedAt { get; set; }

    internal EnvironmentManifest? Manifest { get; set; }

    internal IReadOnlyDictionary<string, string?> BaseEnvironment { get; set; } =
        new Dictionary<string, string?>();

    internal HostExecutionCapabilities HostCapabilities { get; set; } =
        new([options.Profile.HostArchitecture]);

    internal List<PipelineStageResult> Stages { get; } = [];

    internal List<DiscoveryProbe> DiscoveryProbes { get; } = [];

    internal List<Diagnostic> Diagnostics { get; } = [];

    internal List<InstallationDiscovery> Installations { get; } = [];

    internal List<ToolchainCandidate> Candidates { get; } = [];

    internal string? FatalError { get; private set; }

    internal int ExitCode { get; private set; }

    internal bool IsFatal => FatalError is not null;

    internal void SetFatal(string message, int exitCode)
    {
        FatalError ??= message;
        ExitCode = Math.Max(ExitCode, exitCode);
    }

    internal void RecordTestFailure()
    {
        ExitCode = Math.Max(ExitCode, 1);
    }

    internal void RecordCancellation()
    {
        ExitCode = 130;
    }

    internal void AddDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        foreach (Diagnostic diagnostic in diagnostics)
        {
            if (_diagnostics.Add(diagnostic))
            {
                Diagnostics.Add(diagnostic);
            }
        }
    }

    internal IReadOnlyDictionary<string, string?> EnvironmentFor(InstallationManifest? installation)
    {
        var result = new Dictionary<string, string?>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach ((string key, string? value) in BaseEnvironment)
        {
            result[key] = value;
        }

        if (installation is not null)
        {
            foreach ((string key, string? value) in installation.Environment)
            {
                result[key] = value;
            }
        }

        return result;
    }

    internal RuntimeManifest? FindRuntime(RuntimeKind kind, string? installationId = null) =>
        Manifest?.Runtimes.FirstOrDefault(runtime => runtime.Kind == kind
            && (installationId is null || runtime.InstallationId == installationId));

    internal bool RequiredCandidatesSatisfy(
        Func<ToolchainCandidate, bool> predicate) =>
        RequiredCandidatesSatisfy(Candidates, predicate);

    internal bool RequiredCandidatesSatisfy(
        IEnumerable<ToolchainCandidate> candidates,
        Func<ToolchainCandidate, bool> predicate)
    {
        IEnumerable<ToolchainCandidate> required = candidates.Where(
            candidate => candidate.Required);
        return Profile.FailurePolicy.RequireAllRequiredCandidates
            ? required.All(predicate)
            : required.Any(predicate);
    }

    internal bool ContinueAfter(ToolchainCandidate candidate) =>
        !candidate.Failed || Profile.FailurePolicy.ContinueAfterCandidateFailure;

    internal static IReadOnlyDictionary<string, string?> CaptureEnvironment()
    {
        var environment = new Dictionary<string, string?>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = entry.Value?.ToString();
        }

        return environment;
    }
}
