using Incant.Base;
using Incant.Core.Cpp;
using ResourcePurpose = Incant.Core.Cpp.FindSdk.ResourcePurpose;
using SdkKind = Incant.Core.Cpp.FindSdk.Kind;

namespace Incant.AutoTest.CppToolchain;

internal static class EnvironmentProfiles
{
    private static readonly IReadOnlyList<PipelineStageKind> s_fullPipeline =
    [
        PipelineStageKind.Preflight,
        PipelineStageKind.Discover,
        PipelineStageKind.Resolve,
        PipelineStageKind.Validate,
        PipelineStageKind.Build,
        PipelineStageKind.Execute,
    ];

    private static readonly PipelineFailurePolicy s_failurePolicy = new(
        ContinueAfterStageFailure: true,
        ContinueAfterCandidateFailure: true,
        RequireAllRequiredCandidates: true);
    private static readonly IReadOnlyList<InstallationRequirement> s_crossPlatformInstallations =
    [
        Exact("android-25.2.9519653", InstallationKind.AndroidNdk, "25.2.9519653"),
        Exact("android-27.2.12479018", InstallationKind.AndroidNdk, "27.2.12479018"),
        Exact("emscripten-3.1.64", InstallationKind.Emscripten, "3.1.64"),
        Exact("emscripten-6.0.9", InstallationKind.Emscripten, "6.0.9"),
        Exact("wasi-sdk-33", InstallationKind.WasiSdk, "33.0"),
        Exact("wasi-sdk-34", InstallationKind.WasiSdk, "34.0"),
    ];
    private static readonly IReadOnlyList<string> s_emscriptenMultilibs =
        [".", "pic"];
    private static readonly IReadOnlyList<string> s_wasiTargetTriples =
        ["wasm32-wasip1"];
    private static readonly IReadOnlyDictionary<SdkKind, IReadOnlyList<ResourcePurpose>>
        s_requiredSdkResources = new Dictionary<SdkKind, IReadOnlyList<ResourcePurpose>>
        {
            [SdkKind.Gnu] =
            [
                ResourcePurpose.Builtin,
                ResourcePurpose.CppInclude,
                ResourcePurpose.Library,
            ],
            [SdkKind.Llvm] = [ResourcePurpose.Builtin],
            [SdkKind.AppleClang] = [ResourcePurpose.Builtin],
            [SdkKind.Msvc] = [ResourcePurpose.CppInclude, ResourcePurpose.Library],
            [SdkKind.Windows] = [ResourcePurpose.CInclude, ResourcePurpose.Library],
            [SdkKind.Linux] = [ResourcePurpose.CInclude, ResourcePurpose.Library],
            [SdkKind.Sysroot] = [ResourcePurpose.CInclude, ResourcePurpose.Library],
            [SdkKind.Apple] =
            [
                ResourcePurpose.CInclude,
                ResourcePurpose.Library,
                ResourcePurpose.Framework,
            ],
            [SdkKind.AndroidNdk] =
            [
                ResourcePurpose.CInclude,
                ResourcePurpose.CppInclude,
                ResourcePurpose.Library,
            ],
            [SdkKind.Emscripten] =
            [
                ResourcePurpose.CInclude,
                ResourcePurpose.CppInclude,
                ResourcePurpose.Library,
            ],
            [SdkKind.WasiSdk] =
            [
                ResourcePurpose.CInclude,
                ResourcePurpose.CppInclude,
                ResourcePurpose.Library,
            ],
        };

    internal static IReadOnlyList<EnvironmentProfile> All { get; } =
    [
        new EnvironmentProfile
        {
            Name = "windows-vs2022",
            Description = "Runs the Windows Server 2022, VS 2022, and cross-SDK pipeline.",
            HostOS = PlatformOS.Windows,
            HostArchitecture = TargetArchitecture.X64,
            RunnerImage = "windows-2022",
            PipelineStages = s_fullPipeline,
            ExecutionCapabilities = ExecutionCapability.Native
                | ExecutionCapability.Node
                | ExecutionCapability.Wasmtime,
            FailurePolicy = s_failurePolicy,
            RequiredSdkResources = s_requiredSdkResources,
            Installations =
            [
                ProductMajor("vs2022", InstallationKind.VisualStudio, 17),
                ExactSdk("windows-sdk-17763", InstallationKind.WindowsSdk, "10.0.17763.0"),
                ExactSdk("windows-sdk-19041", InstallationKind.WindowsSdk, "10.0.19041.0"),
                ExactSdk("windows-sdk-22621", InstallationKind.WindowsSdk, "10.0.22621.0"),
                ExactSdk("windows-sdk-26100", InstallationKind.WindowsSdk, "10.0.26100.0"),
                CompilerMajor("llvm-20", InstallationKind.Llvm, 20),
                .. s_crossPlatformInstallations,
            ],
            WindowsMsvcArchitectures = [TargetArchitecture.X64, TargetArchitecture.ARM64],
            WindowsLlvmArchitectures = [TargetArchitecture.X64, TargetArchitecture.ARM64],
            AndroidArchitectures = [TargetArchitecture.ARM64, TargetArchitecture.X64],
            EmscriptenMultilibs = s_emscriptenMultilibs,
            WasiTargetTriples = s_wasiTargetTriples,
        },
        new EnvironmentProfile
        {
            Name = "windows-vs2026",
            Description = "Runs the Windows Server 2025 and Visual Studio 2026 pipeline.",
            HostOS = PlatformOS.Windows,
            HostArchitecture = TargetArchitecture.X64,
            RunnerImage = "windows-2025-vs2026",
            PipelineStages = s_fullPipeline,
            ExecutionCapabilities = ExecutionCapability.Native,
            FailurePolicy = s_failurePolicy,
            RequiredSdkResources = s_requiredSdkResources,
            Installations =
            [
                ProductMajor("vs2026", InstallationKind.VisualStudio, 18),
                ExactSdk("windows-sdk-26100", InstallationKind.WindowsSdk, "10.0.26100.0"),
                CompilerMajor("llvm-20", InstallationKind.Llvm, 20),
            ],
            WindowsMsvcArchitectures = [TargetArchitecture.X64, TargetArchitecture.ARM64],
            WindowsLlvmArchitectures = [TargetArchitecture.X64],
            UseExistingMsvcTargetsForNonDefaultToolSets = true,
        },
        new EnvironmentProfile
        {
            Name = "ubuntu-24.04",
            Description = "Runs the Ubuntu 24.04 native, multilib, and cross-SDK pipeline.",
            HostOS = PlatformOS.Linux,
            HostArchitecture = TargetArchitecture.X64,
            RunnerImage = "ubuntu-24.04",
            PipelineStages = s_fullPipeline,
            ExecutionCapabilities = ExecutionCapability.Native
                | ExecutionCapability.Node
                | ExecutionCapability.Wasmtime,
            FailurePolicy = s_failurePolicy,
            RequiredSdkResources = s_requiredSdkResources,
            Installations =
            [
                CompilerMajor("gcc-12", InstallationKind.Gnu, 12),
                CompilerMajor("gcc-13", InstallationKind.Gnu, 13),
                CompilerMajor("gcc-14", InstallationKind.Gnu, 14),
                CompilerMajor("clang-16", InstallationKind.Llvm, 16),
                CompilerMajor("clang-17", InstallationKind.Llvm, 17),
                CompilerMajor("clang-18", InstallationKind.Llvm, 18),
                .. s_crossPlatformInstallations,
            ],
            NativeArchitectures = [TargetArchitecture.X64],
            TestAllGnuMultilibs = true,
            AndroidArchitectures = [TargetArchitecture.ARM64, TargetArchitecture.X64],
            EmscriptenMultilibs = s_emscriptenMultilibs,
            WasiTargetTriples = s_wasiTargetTriples,
        },
        new EnvironmentProfile
        {
            Name = "macos-15-arm64",
            Description = "Runs the macOS 15 ARM64 native, Apple platform, and cross-SDK pipeline.",
            HostOS = PlatformOS.OSX,
            HostArchitecture = TargetArchitecture.ARM64,
            RunnerImage = "macos-15",
            PipelineStages = s_fullPipeline,
            ExecutionCapabilities = ExecutionCapability.Native
                | ExecutionCapability.Node
                | ExecutionCapability.Wasmtime,
            FailurePolicy = s_failurePolicy,
            RequiredSdkResources = s_requiredSdkResources,
            Installations =
            [
                ProductMinor("xcode-16.4", InstallationKind.Xcode, "16.4"),
                ProductMinor("xcode-26.3", InstallationKind.Xcode, "26.3"),
                CompilerMajor("gcc-14", InstallationKind.Gnu, 14),
                CompilerMajor("llvm-18", InstallationKind.Llvm, 18),
                .. s_crossPlatformInstallations,
            ],
            NativeArchitectures = [TargetArchitecture.ARM64],
            ApplePlatforms = [TargetPlatform.MacOS, TargetPlatform.IOS, TargetPlatform.IOSSimulator],
            AppleArchitectures = [TargetArchitecture.ARM64],
            AndroidArchitectures = [TargetArchitecture.ARM64, TargetArchitecture.X64],
            EmscriptenMultilibs = s_emscriptenMultilibs,
            WasiTargetTriples = s_wasiTargetTriples,
        },
    ];

    internal static EnvironmentProfile? Find(string name) =>
        All.FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.Ordinal));

    private static InstallationRequirement Exact(string id, InstallationKind kind, string version)
    {
        var rule = new VersionRule(version, VersionSource.Version, VersionPrecision.Exact);
        return new InstallationRequirement(id, kind, rule, rule);
    }

    private static InstallationRequirement ExactSdk(string id, InstallationKind kind, string version) =>
        new(id, kind, null, new VersionRule(version, VersionSource.Version, VersionPrecision.Exact));

    private static InstallationRequirement CompilerMajor(string id, InstallationKind kind, int major) =>
        new(
            id,
            kind,
            new VersionRule(major.ToString(System.Globalization.CultureInfo.InvariantCulture),
                VersionSource.CompilerVersion, VersionPrecision.Major),
            new VersionRule(major.ToString(System.Globalization.CultureInfo.InvariantCulture),
                VersionSource.Version, VersionPrecision.Major));

    private static InstallationRequirement ProductMajor(string id, InstallationKind kind, int major)
    {
        var rule = new VersionRule(
            major.ToString(System.Globalization.CultureInfo.InvariantCulture),
            VersionSource.ProductVersion,
            VersionPrecision.Major);
        return new InstallationRequirement(id, kind, rule, rule);
    }

    private static InstallationRequirement ProductMinor(string id, InstallationKind kind, string version)
    {
        var rule = new VersionRule(version, VersionSource.ProductVersion, VersionPrecision.Minor);
        return new InstallationRequirement(id, kind, rule, rule);
    }
}
