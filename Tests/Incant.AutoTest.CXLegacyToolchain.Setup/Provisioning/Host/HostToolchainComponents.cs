using System.Globalization;

namespace Incant.AutoTest.CXLegacyToolchain.Setup;

internal static class HostToolchainComponents
{
    internal static IReadOnlyList<ISetupComponent> Create(EnvironmentDefinition profile)
    {
        if (profile.HostOS == Incant.Base.PlatformOS.Linux)
        {
            return
            [
                new UbuntuPackagesComponent(profile.Installations),
                .. profile.Installations
                    .Where(requirement => requirement.Kind is
                        InstallationKind.Gnu or InstallationKind.Llvm)
                    .Select(CreateLinuxCompilerComponent),
            ];
        }

        return [];
    }

    private static ISetupComponent CreateLinuxCompilerComponent(
        InstallationRequirement requirement)
    {
        if (requirement.Provisioning == ProvisioningMethod.Linuxbrew)
        {
            return new LinuxbrewLlvmComponent(requirement);
        }

        int major = HostToolchainUtilities.Major(requirement);
        return new CompilerInventoryComponent(
            requirement,
            requirement.Kind == InstallationKind.Gnu
                ? [$"gcc-{major}"]
                : [$"clang-{major}"],
            requirement.Kind == InstallationKind.Gnu
                ? [$"g++-{major}"]
                : [$"clang++-{major}"],
            dependencies: ["ubuntu-packages"]);
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
