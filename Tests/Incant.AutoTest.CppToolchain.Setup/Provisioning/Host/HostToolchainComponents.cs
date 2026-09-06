using System.Globalization;

namespace Incant.AutoTest.CppToolchain.Setup;

internal static class HostToolchainComponents
{
    internal static IReadOnlyList<ISetupComponent> Create(EnvironmentDefinition profile)
    {
        if (profile.HostOS == Incant.Base.PlatformOS.Windows)
        {
            return profile.Installations
                .Where(requirement => requirement.Kind is
                    InstallationKind.VisualStudio or
                    InstallationKind.WindowsSdk or
                    InstallationKind.Llvm)
                .Select(CreateWindowsComponent)
                .ToArray();
        }

        if (profile.HostOS == Incant.Base.PlatformOS.Linux)
        {
            return
            [
                new UbuntuPackagesComponent(),
                .. profile.Installations
                    .Where(requirement => requirement.Kind is
                        InstallationKind.Gnu or InstallationKind.Llvm)
                    .Select(requirement => new CompilerInventoryComponent(
                        requirement,
                        requirement.Kind == InstallationKind.Gnu
                            ? [$"gcc-{HostToolchainUtilities.Major(requirement)}"]
                            : [$"clang-{HostToolchainUtilities.Major(requirement)}"],
                        requirement.Kind == InstallationKind.Gnu
                            ? [$"g++-{HostToolchainUtilities.Major(requirement)}"]
                            : [$"clang++-{HostToolchainUtilities.Major(requirement)}"],
                        dependencies: ["ubuntu-packages"])),
            ];
        }

        if (profile.HostOS == Incant.Base.PlatformOS.OSX)
        {
            return profile.Installations
                .Where(requirement => requirement.Kind is
                    InstallationKind.Xcode or
                    InstallationKind.Gnu or
                    InstallationKind.Llvm)
                .Select(CreateMacComponent)
                .ToArray();
        }

        throw new SetupConfigurationException(
            $"No host provisioning plan is defined for '{profile.Name}'.");
    }

    private static ISetupComponent CreateWindowsComponent(
        InstallationRequirement requirement) => requirement.Kind switch
        {
            InstallationKind.VisualStudio => new VisualStudioInventoryComponent(
                requirement.Id,
                HostToolchainUtilities.Major(requirement)),
            InstallationKind.WindowsSdk => new WindowsSdkInventoryComponent(
                requirement.Id,
                requirement.SdkVersion?.Value
                    ?? throw new SetupConfigurationException(
                        $"Windows SDK requirement '{requirement.Id}' has no SDK version.")),
            InstallationKind.Llvm => new WindowsLlvmInventoryComponent(
                requirement.Id,
                HostToolchainUtilities.Major(requirement)),
            _ => throw new ArgumentOutOfRangeException(nameof(requirement), requirement.Kind, null),
        };

    private static ISetupComponent CreateMacComponent(
        InstallationRequirement requirement) => requirement.Kind switch
        {
            InstallationKind.Xcode => new XcodeInventoryComponent(
                requirement.Id,
                requirement.SdkVersion?.Value
                    ?? throw new SetupConfigurationException(
                        $"Xcode requirement '{requirement.Id}' has no product version.")),
            InstallationKind.Gnu => CreateMacGnuComponent(requirement),
            InstallationKind.Llvm => new MacLlvmInventoryComponent(
                requirement.Id,
                HostToolchainUtilities.Major(requirement)),
            _ => throw new ArgumentOutOfRangeException(nameof(requirement), requirement.Kind, null),
        };

    private static ISetupComponent CreateMacGnuComponent(
        InstallationRequirement requirement)
    {
        int major = HostToolchainUtilities.Major(requirement);
        return new CompilerInventoryComponent(
            requirement,
            [$"/opt/homebrew/bin/gcc-{major}", $"gcc-{major}"],
            [$"/opt/homebrew/bin/g++-{major}", $"g++-{major}"],
            dependencies: []);
    }
}

internal static class HostToolchainUtilities
{
    internal static int Major(InstallationRequirement requirement)
    {
        string? value = requirement.ToolVersion?.Value ?? requirement.SdkVersion?.Value;
        if (value is null
            || !int.TryParse(
                value.Split('.')[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int major))
        {
            throw new SetupConfigurationException(
                $"Requirement '{requirement.Id}' has no parseable major version.");
        }

        return major;
    }

    internal static void RequireMajor(string executable, string version, int expectedMajor)
    {
        if (ProgramLocator.VersionMajor(version) != expectedMajor)
        {
            throw new InvalidDataException(
                $"'{executable}' reports version {version}; expected major {expectedMajor}.");
        }
    }

    internal static string PrependPath(string directory)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        return string.IsNullOrEmpty(path)
            ? directory
            : $"{directory}{Path.PathSeparator}{path}";
    }
}
