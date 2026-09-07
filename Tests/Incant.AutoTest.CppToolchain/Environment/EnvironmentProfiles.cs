using Incant.Core.Cpp;

namespace Incant.AutoTest.CppToolchain;

internal static class EnvironmentProfiles
{
    private static readonly IReadOnlyList<string> s_emscriptenMultilibs = [".", "pic"];
    private static readonly IReadOnlyList<string> s_wasiTargetTriples = ["wasm32-wasip1"];

    internal static IReadOnlyList<EnvironmentProfile> All { get; } =
    [
        new EnvironmentProfile
        {
            Definition = EnvironmentDefinitions.WindowsVs2022,
            ExecutionCapabilities = ExecutionCapability.Native
                | ExecutionCapability.Node
                | ExecutionCapability.Wasmtime,
            WindowsMsvcArchitectures = [TargetArchitecture.X64, TargetArchitecture.ARM64],
            WindowsLlvmArchitectures = [TargetArchitecture.X64, TargetArchitecture.ARM64],
            AndroidArchitectures = [TargetArchitecture.ARM64, TargetArchitecture.X64],
            EmscriptenMultilibs = s_emscriptenMultilibs,
            WasiTargetTriples = s_wasiTargetTriples,
        },
        new EnvironmentProfile
        {
            Definition = EnvironmentDefinitions.WindowsVs2026,
            ExecutionCapabilities = ExecutionCapability.Native,
            WindowsMsvcArchitectures = [TargetArchitecture.X64, TargetArchitecture.ARM64],
            WindowsLlvmArchitectures = [TargetArchitecture.X64],
        },
        new EnvironmentProfile
        {
            Definition = EnvironmentDefinitions.Ubuntu2404,
            ExecutionCapabilities = ExecutionCapability.Native
                | ExecutionCapability.Node
                | ExecutionCapability.Wasmtime,
            NativeArchitectures = [TargetArchitecture.X64],
            TestAllGnuMultilibs = true,
            AndroidArchitectures = [TargetArchitecture.ARM64, TargetArchitecture.X64],
            EmscriptenMultilibs = s_emscriptenMultilibs,
            WasiTargetTriples = s_wasiTargetTriples,
        },
        new EnvironmentProfile
        {
            Definition = EnvironmentDefinitions.MacOS15Arm64,
            ExecutionCapabilities = ExecutionCapability.Native
                | ExecutionCapability.Node
                | ExecutionCapability.Wasmtime,
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
