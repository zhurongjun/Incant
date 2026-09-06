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
    private static readonly IReadOnlyList<string> s_emscriptenMultilibs = [".", "pic"];
    private static readonly IReadOnlyList<string> s_wasiTargetTriples = ["wasm32-wasip1"];
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
            Definition = EnvironmentDefinitions.WindowsVs2022,
            PipelineStages = s_fullPipeline,
            ExecutionCapabilities = ExecutionCapability.Native
                | ExecutionCapability.Node
                | ExecutionCapability.Wasmtime,
            FailurePolicy = s_failurePolicy,
            RequiredSdkResources = s_requiredSdkResources,
            WindowsMsvcArchitectures = [TargetArchitecture.X64, TargetArchitecture.ARM64],
            WindowsLlvmArchitectures = [TargetArchitecture.X64, TargetArchitecture.ARM64],
            AndroidArchitectures = [TargetArchitecture.ARM64, TargetArchitecture.X64],
            EmscriptenMultilibs = s_emscriptenMultilibs,
            WasiTargetTriples = s_wasiTargetTriples,
        },
        new EnvironmentProfile
        {
            Definition = EnvironmentDefinitions.WindowsVs2026,
            PipelineStages = s_fullPipeline,
            ExecutionCapabilities = ExecutionCapability.Native,
            FailurePolicy = s_failurePolicy,
            RequiredSdkResources = s_requiredSdkResources,
            WindowsMsvcArchitectures = [TargetArchitecture.X64, TargetArchitecture.ARM64],
            WindowsLlvmArchitectures = [TargetArchitecture.X64],
            UseExistingMsvcTargetsForNonDefaultToolSets = true,
        },
        new EnvironmentProfile
        {
            Definition = EnvironmentDefinitions.Ubuntu2404,
            PipelineStages = s_fullPipeline,
            ExecutionCapabilities = ExecutionCapability.Native
                | ExecutionCapability.Node
                | ExecutionCapability.Wasmtime,
            FailurePolicy = s_failurePolicy,
            RequiredSdkResources = s_requiredSdkResources,
            NativeArchitectures = [TargetArchitecture.X64],
            TestAllGnuMultilibs = true,
            AndroidArchitectures = [TargetArchitecture.ARM64, TargetArchitecture.X64],
            EmscriptenMultilibs = s_emscriptenMultilibs,
            WasiTargetTriples = s_wasiTargetTriples,
        },
        new EnvironmentProfile
        {
            Definition = EnvironmentDefinitions.MacOS15Arm64,
            PipelineStages = s_fullPipeline,
            ExecutionCapabilities = ExecutionCapability.Native
                | ExecutionCapability.Node
                | ExecutionCapability.Wasmtime,
            FailurePolicy = s_failurePolicy,
            RequiredSdkResources = s_requiredSdkResources,
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
}
