using Incant.Base;
using Incant.CXLegacy;

namespace Incant.AutoTest.CXLegacyToolchain;

internal enum PipelineStageKind
{
    Preflight,
    Discover,
    Resolve,
    Build,
    Execute,
}

[Flags]
internal enum ExecutionCapability
{
    None = 0,
    Native = 1 << 0,
    Node = 1 << 1,
    Wasmtime = 1 << 2,
}

internal sealed class EnvironmentProfile
{
    internal required EnvironmentDefinition Definition { get; init; }

    internal string Name => Definition.Name;

    internal string Description => Definition.Description;

    internal PlatformOS HostOS => Definition.HostOS;

    internal TargetArchitecture HostArchitecture => Definition.HostArchitecture;

    internal string RunnerImage => Definition.RunnerImage;

    internal IReadOnlyList<InstallationRequirement> Installations => Definition.Installations;

    internal required ExecutionCapability ExecutionCapabilities { get; init; }

    internal IReadOnlyList<TargetArchitecture> NativeArchitectures { get; init; } = [];

    internal bool TestAllGnuMultilibs { get; init; }

    internal IReadOnlyList<TargetArchitecture> WindowsMsvcArchitectures { get; init; } = [];

    internal IReadOnlyList<TargetArchitecture> WindowsLlvmArchitectures { get; init; } = [];

    internal IReadOnlyList<TargetPlatform> ApplePlatforms { get; init; } = [];

    internal IReadOnlyList<TargetArchitecture> AppleArchitectures { get; init; } = [];

    internal IReadOnlyList<TargetArchitecture> AndroidArchitectures { get; init; } = [];

    internal int AndroidApi { get; init; } = 21;

    internal IReadOnlyList<string> EmscriptenMultilibs { get; init; } = [];

    internal IReadOnlyList<string> WasiTargetTriples { get; init; } = [];

    internal bool SupportsExecution(ExecutionMode mode) => mode switch
    {
        ExecutionMode.BuildOnly => true,
        ExecutionMode.Native => ExecutionCapabilities.HasFlag(ExecutionCapability.Native),
        ExecutionMode.Node => ExecutionCapabilities.HasFlag(ExecutionCapability.Node),
        ExecutionMode.Wasmtime => ExecutionCapabilities.HasFlag(ExecutionCapability.Wasmtime),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
