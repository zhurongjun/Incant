using Incant.Base;
using Incant.Core.Cpp;
using ResourcePurpose = Incant.Core.Cpp.FindSdk.ResourcePurpose;
using SdkKind = Incant.Core.Cpp.FindSdk.Kind;
using SdkQuery = Incant.Core.Cpp.FindSdk.SdkQuery;
using ToolSet = Incant.Core.Cpp.FindTools.ToolSet;
using ToolSetQuery = Incant.Core.Cpp.FindTools.ToolSetQuery;

namespace Incant.AutoTest.CppToolchain;

internal enum InstallationKind
{
    VisualStudio,
    WindowsSdk,
    Gnu,
    Llvm,
    Xcode,
    AndroidNdk,
    Emscripten,
    WasiSdk,
}

internal enum RuntimeKind
{
    Node,
    Wasmtime,
    Python,
}

internal enum VersionSource
{
    Version,
    ProductVersion,
    CompilerVersion,
}

internal enum VersionPrecision
{
    Exact,
    Major,
    Minor,
}

internal enum PipelineStageKind
{
    Preflight,
    Discover,
    Resolve,
    Validate,
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

internal sealed record PipelineFailurePolicy(
    bool ContinueAfterStageFailure,
    bool ContinueAfterCandidateFailure,
    bool RequireAllRequiredCandidates);

internal sealed record VersionRule(
    string Value,
    VersionSource Source,
    VersionPrecision Precision)
{
    internal VersionConstraint Constraint
    {
        get
        {
            Version version = Parse(Value);
            return Precision switch
            {
                VersionPrecision.Exact => new VersionConstraint(exact: version),
                VersionPrecision.Major => new VersionConstraint(
                    minimumInclusive: new Version(version.Major, 0),
                    maximumExclusive: new Version(version.Major + 1, 0)),
                VersionPrecision.Minor => new VersionConstraint(
                    minimumInclusive: new Version(version.Major, version.Minor),
                    maximumExclusive: new Version(version.Major, version.Minor + 1)),
                _ => throw new ArgumentOutOfRangeException(nameof(Precision), Precision, null),
            };
        }
    }

    internal bool Matches(ToolSet toolSet)
    {
        Version? actual = Source switch
        {
            VersionSource.Version => toolSet.Version,
            VersionSource.ProductVersion => toolSet.ProductVersion,
            VersionSource.CompilerVersion => toolSet.CompilerVersion,
            _ => throw new ArgumentOutOfRangeException(nameof(Source), Source, null),
        };
        return Constraint.Matches(actual);
    }

    internal ToolSetQuery Apply(ToolSetQuery query) => Source switch
    {
        VersionSource.Version => query with { Version = Constraint },
        VersionSource.ProductVersion => query with { ProductVersion = Constraint },
        VersionSource.CompilerVersion => query with { CompilerVersion = Constraint },
        _ => throw new ArgumentOutOfRangeException(nameof(Source), Source, null),
    };

    internal SdkQuery Apply(SdkQuery query) => Source switch
    {
        VersionSource.Version or VersionSource.CompilerVersion => query with { Version = Constraint },
        VersionSource.ProductVersion => query with { ProductVersion = Constraint },
        _ => throw new ArgumentOutOfRangeException(nameof(Source), Source, null),
    };

    private static Version Parse(string value)
    {
        string normalized = value.Contains('.', StringComparison.Ordinal) ? value : value + ".0";
        return Version.Parse(normalized);
    }
}

internal sealed record InstallationRequirement(
    string Id,
    InstallationKind Kind,
    VersionRule? ToolVersion,
    VersionRule? SdkVersion,
    bool Required = true);

internal sealed class EnvironmentProfile
{
    internal required string Name { get; init; }

    internal required string Description { get; init; }

    internal required PlatformOS HostOS { get; init; }

    internal required TargetArchitecture HostArchitecture { get; init; }

    internal required string RunnerImage { get; init; }

    internal required IReadOnlyList<PipelineStageKind> PipelineStages { get; init; }

    internal required ExecutionCapability ExecutionCapabilities { get; init; }

    internal required PipelineFailurePolicy FailurePolicy { get; init; }

    internal required IReadOnlyList<InstallationRequirement> Installations { get; init; }

    internal required IReadOnlyDictionary<SdkKind, IReadOnlyList<ResourcePurpose>>
        RequiredSdkResources
    { get; init; }

    internal IReadOnlyList<TargetArchitecture> NativeArchitectures { get; init; } = [];

    internal bool TestAllGnuMultilibs { get; init; }

    internal IReadOnlyList<TargetArchitecture> WindowsMsvcArchitectures { get; init; } = [];

    internal IReadOnlyList<TargetArchitecture> WindowsLlvmArchitectures { get; init; } = [];

    internal bool UseExistingMsvcTargetsForNonDefaultToolSets { get; init; }

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
