using System.Runtime.InteropServices;
using Incant.Base;
using Incant.Core.Cpp;

namespace Incant.AutoTest.CppToolchain.Setup;

internal sealed class SetupContext
{
    private readonly List<InstallationManifest> _installations = [];
    private readonly List<RuntimeManifest> _runtimes = [];
    private readonly List<SetupStageRecord> _stages = [];
    private readonly List<SetupComponentRecord> _components = [];
    private readonly List<SetupCommandRecord> _commands = [];
    private readonly Dictionary<string, SetupResultStatus> _componentStatuses =
        new(StringComparer.Ordinal);

    internal SetupContext(SetupOptions options)
    {
        Options = options;
        Paths = new SetupPathGuard(options.ToolchainRoot);
        Commands = new SetupCommandRunner(this);
        Probes = new InstallationProbeVerifier(Commands);
        Downloads = new DownloadCache(this);
        Archives = new ArchiveInstaller(this);
    }

    internal SetupOptions Options { get; }

    internal EnvironmentDefinition Profile => Options.Profile;

    internal SetupPathGuard Paths { get; }

    internal SetupCommandRunner Commands { get; }

    internal InstallationProbeVerifier Probes { get; }

    internal DownloadCache Downloads { get; }

    internal ArchiveInstaller Archives { get; }

    internal DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    internal DateTimeOffset? CompletedAt { get; set; }

    internal IReadOnlyList<InstallationManifest> Installations => _installations;

    internal IReadOnlyList<RuntimeManifest> Runtimes => _runtimes;

    internal IReadOnlyList<SetupStageRecord> Stages => _stages;

    internal IReadOnlyList<SetupComponentRecord> Components => _components;

    internal IReadOnlyList<SetupCommandRecord> CommandRecords => _commands;

    internal string StateRoot => Path.Combine(Options.WorkRoot, "state");

    internal string PendingManifestPath =>
        Path.Combine(StateRoot, "environment.pending.json");

    internal string? FatalError { get; private set; }

    internal string? ActiveStage { get; set; }

    internal string? ActiveComponentId { get; set; }

    internal int ExitCode { get; private set; }

    internal bool HasComponentFailures =>
        _components.Any(component => component.Status is
            SetupResultStatus.Failed or
            SetupResultStatus.Skipped or
            SetupResultStatus.Cancelled);

    internal bool BuildSucceeded { get; set; }

    internal bool ManifestPrepared { get; set; }

    internal bool EnvironmentExported { get; set; }

    internal SetupStageRecord AddStage(string name)
    {
        var record = new SetupStageRecord { Name = name };
        _stages.Add(record);
        return record;
    }

    internal SetupComponentRecord AddComponent(
        string stage,
        ISetupComponent component)
    {
        var record = new SetupComponentRecord
        {
            Id = component.Id,
            Name = component.Name,
            Stage = stage,
            Dependencies = component.Dependencies,
        };
        _components.Add(record);
        _componentStatuses.Add(component.Id, SetupResultStatus.Pending);
        return record;
    }

    internal SetupResultStatus? GetComponentStatus(string id) =>
        _componentStatuses.TryGetValue(id, out SetupResultStatus status) ? status : null;

    internal void SetComponentStatus(string id, SetupResultStatus status) =>
        _componentStatuses[id] = status;

    internal SetupCommandRecord AddCommand(SetupCommandRecord command)
    {
        _commands.Add(command);
        return command;
    }

    internal int NextCommandSequence => _commands.Count + 1;

    internal void Merge(ProvisioningResult result)
    {
        string[] installationIds = result.Installations
            .Select(installation => installation.Id)
            .ToArray();
        string[] runtimeIds = result.Runtimes
            .Select(runtime => runtime.Id)
            .ToArray();
        string? duplicateInstallation = installationIds
            .GroupBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        string? duplicateRuntime = runtimeIds
            .GroupBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        string? existingInstallation = installationIds.FirstOrDefault(id =>
            _installations.Any(existing =>
                string.Equals(existing.Id, id, StringComparison.Ordinal)));
        string? existingRuntime = runtimeIds.FirstOrDefault(id =>
            _runtimes.Any(existing =>
                string.Equals(existing.Id, id, StringComparison.Ordinal)));
        if (duplicateInstallation is not null || existingInstallation is not null)
        {
            throw new InvalidOperationException(
                $"Installation '{duplicateInstallation ?? existingInstallation}' "
                + "was registered more than once.");
        }

        if (duplicateRuntime is not null || existingRuntime is not null)
        {
            throw new InvalidOperationException(
                $"Runtime '{duplicateRuntime ?? existingRuntime}' was registered more than once.");
        }

        _installations.AddRange(result.Installations);
        _runtimes.AddRange(result.Runtimes);
    }

    internal EnvironmentManifest CreateManifest() => new()
    {
        SchemaVersion = EnvironmentManifest.CurrentSchemaVersion,
        Profile = Profile.Name,
        Runner = new RunnerManifest
        {
            ImageLabel = Profile.RunnerImage,
            ImageOS = Environment.GetEnvironmentVariable("ImageOS") ?? string.Empty,
            ImageVersion = Environment.GetEnvironmentVariable("ImageVersion") ?? string.Empty,
            OS = Environment.GetEnvironmentVariable("RUNNER_OS") ?? HostOSName(),
            Architecture = Environment.GetEnvironmentVariable("RUNNER_ARCH") ?? HostArchitectureName(),
        },
        Environment = new Dictionary<string, string?>(),
        Installations = _installations.ToArray(),
        Runtimes = _runtimes.ToArray(),
    };

    internal void RecordFailure(string message, int exitCode = 1)
    {
        FatalError ??= message;
        ExitCode = Math.Max(ExitCode, exitCode);
    }

    internal void RecordCancellation()
    {
        FatalError = "Toolchain setup was cancelled.";
        ExitCode = 130;
    }

    private static string HostOSName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        return RuntimeInformation.OSDescription;
    }

    private static string HostArchitectureName() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => TargetArchitecture.X64.ToString(),
        Architecture.Arm64 => TargetArchitecture.ARM64.ToString(),
        Architecture.X86 => TargetArchitecture.X86.ToString(),
        Architecture.Arm => TargetArchitecture.ARM.ToString(),
        Architecture.Wasm => TargetArchitecture.Wasm32.ToString(),
        Architecture.S390x or Architecture.LoongArch64 or Architecture.Armv6
            or Architecture.Ppc64le => RuntimeInformation.OSArchitecture.ToString(),
        _ => RuntimeInformation.OSArchitecture.ToString(),
    };
}
