using System.Globalization;
using Incant.Base;
using Incant.Core.Cpp;

namespace Incant.AutoTest.CppToolchain.Shared;

internal static class EnvironmentDefinitions
{
    private static readonly IReadOnlyList<InstallationRequirement> s_crossPlatformInstallations =
    [
        Exact("android-25.2.9519653", InstallationKind.AndroidNdk, "25.2.9519653"),
        Exact("android-27.2.12479018", InstallationKind.AndroidNdk, "27.2.12479018"),
        Exact("emscripten-3.1.64", InstallationKind.Emscripten, "3.1.64"),
        Exact("emscripten-6.0.9", InstallationKind.Emscripten, "6.0.9"),
        Exact("wasi-sdk-33", InstallationKind.WasiSdk, "33.0"),
        Exact("wasi-sdk-34", InstallationKind.WasiSdk, "34.0"),
    ];

    internal static EnvironmentDefinition WindowsVs2022 { get; } = new()
    {
        Name = "windows-vs2022",
        Description = "Runs the Windows Server 2022, VS 2022, and cross-SDK pipeline.",
        HostOS = PlatformOS.Windows,
        HostArchitecture = TargetArchitecture.X64,
        RunnerImage = "windows-2022",
        RequiredHostFamilies = [InstallationKind.VisualStudio, InstallationKind.WindowsSdk, InstallationKind.Llvm],
        Installations = s_crossPlatformInstallations,
    };

    internal static EnvironmentDefinition WindowsVs2026 { get; } = new()
    {
        Name = "windows-vs2026",
        Description = "Runs the Windows Server 2025 and Visual Studio 2026 pipeline.",
        HostOS = PlatformOS.Windows,
        HostArchitecture = TargetArchitecture.X64,
        RunnerImage = "windows-2025-vs2026",
        RequiredHostFamilies = [InstallationKind.VisualStudio, InstallationKind.WindowsSdk, InstallationKind.Llvm],
        Installations = [],
    };

    internal static EnvironmentDefinition Ubuntu2404 { get; } = new()
    {
        Name = "ubuntu-24.04",
        Description = "Runs the Ubuntu 24.04 native, multilib, and cross-SDK pipeline.",
        HostOS = PlatformOS.Linux,
        HostArchitecture = TargetArchitecture.X64,
        RunnerImage = "ubuntu-24.04",
        RequiredHostFamilies = [InstallationKind.Gnu, InstallationKind.Llvm],
        Installations =
        [
            CompilerMajor("gcc-12", InstallationKind.Gnu, 12),
            CompilerMajor("gcc-13", InstallationKind.Gnu, 13),
            CompilerMajor("gcc-14", InstallationKind.Gnu, 14),
            CompilerMajor("clang-16", InstallationKind.Llvm, 16),
            CompilerMajor("clang-17", InstallationKind.Llvm, 17),
            CompilerMajor("clang-18", InstallationKind.Llvm, 18),
            CompilerExact(
                "linuxbrew-llvm-18",
                InstallationKind.Llvm,
                "18.1.8") with { Provisioning = ProvisioningMethod.Linuxbrew },
            .. s_crossPlatformInstallations,
        ],
    };

    internal static EnvironmentDefinition MacOS15Arm64 { get; } = new()
    {
        Name = "macos-15-arm64",
        Description = "Runs the macOS 15 ARM64 native, Apple platform, and cross-SDK pipeline.",
        HostOS = PlatformOS.OSX,
        HostArchitecture = TargetArchitecture.ARM64,
        RunnerImage = "macos-15",
        RequiredHostFamilies = [InstallationKind.Xcode, InstallationKind.Gnu, InstallationKind.Llvm],
        Installations = s_crossPlatformInstallations,
    };

    internal static IReadOnlyList<EnvironmentDefinition> All { get; } =
    [
        WindowsVs2022,
        WindowsVs2026,
        Ubuntu2404,
        MacOS15Arm64,
    ];

    private static InstallationRequirement Exact(string id, InstallationKind kind, string version)
    {
        var rule = new VersionRule(version, VersionSource.Version, VersionPrecision.Exact);
        return new InstallationRequirement(id, kind, rule, rule);
    }

    private static InstallationRequirement CompilerExact(
        string id,
        InstallationKind kind,
        string version) => new(
            id,
            kind,
            new VersionRule(
                version,
                VersionSource.CompilerVersion,
                VersionPrecision.Exact),
            new VersionRule(
                version,
                VersionSource.Version,
                VersionPrecision.Exact));

    private static InstallationRequirement CompilerMajor(string id, InstallationKind kind, int major)
    {
        string value = major.ToString(CultureInfo.InvariantCulture);
        return new InstallationRequirement(
            id,
            kind,
            new VersionRule(value, VersionSource.CompilerVersion, VersionPrecision.Major),
            new VersionRule(value, VersionSource.Version, VersionPrecision.Major));
    }
}
