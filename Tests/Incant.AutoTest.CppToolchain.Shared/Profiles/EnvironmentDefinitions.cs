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
    };

    internal static EnvironmentDefinition WindowsVs2026 { get; } = new()
    {
        Name = "windows-vs2026",
        Description = "Runs the Windows Server 2025 and Visual Studio 2026 pipeline.",
        HostOS = PlatformOS.Windows,
        HostArchitecture = TargetArchitecture.X64,
        RunnerImage = "windows-2025-vs2026",
        Installations =
        [
            ProductMajor("vs2026", InstallationKind.VisualStudio, 18),
            ExactSdk("windows-sdk-26100", InstallationKind.WindowsSdk, "10.0.26100.0"),
            CompilerMajor("llvm-20", InstallationKind.Llvm, 20),
        ],
    };

    internal static EnvironmentDefinition Ubuntu2404 { get; } = new()
    {
        Name = "ubuntu-24.04",
        Description = "Runs the Ubuntu 24.04 native, multilib, and cross-SDK pipeline.",
        HostOS = PlatformOS.Linux,
        HostArchitecture = TargetArchitecture.X64,
        RunnerImage = "ubuntu-24.04",
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
                "18.1.8"),
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
        Installations =
        [
            ProductMinor("xcode-16.4", InstallationKind.Xcode, "16.4"),
            ProductMinor("xcode-26.3", InstallationKind.Xcode, "26.3"),
            CompilerMajor("gcc-14", InstallationKind.Gnu, 14),
            CompilerMajor("llvm-18", InstallationKind.Llvm, 18),
            .. s_crossPlatformInstallations,
        ],
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

    private static InstallationRequirement ExactSdk(string id, InstallationKind kind, string version) =>
        new(id, kind, null, new VersionRule(version, VersionSource.Version, VersionPrecision.Exact));

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

    private static InstallationRequirement ProductMajor(string id, InstallationKind kind, int major)
    {
        var rule = new VersionRule(
            major.ToString(CultureInfo.InvariantCulture),
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
