using System.Text.RegularExpressions;

namespace Incant.AutoTest.CppToolchain.Setup;

internal sealed partial class XcodeInventoryComponent(string id, string version)
    : ISetupComponent
{
    public string Id => id;

    public string Name => $"Xcode {version} inventory";

    public IReadOnlyList<string> Dependencies => [];

    public async Task<ProvisioningResult> ProvisionAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        string developerRoot = SetupPathGuard.RequireDirectory(
            $"/Applications/Xcode_{version}.app/Contents/Developer",
            $"Xcode {version}");
        string xcodebuild = SetupPathGuard.RequireFile(
            "/usr/bin/xcodebuild",
            "xcodebuild");
        SetupCommandOutput output = await context.Commands.RunAsync(
            xcodebuild,
            ["-version"],
            new SetupCommandOptions(
                Environment: new Dictionary<string, string?>
                {
                    ["DEVELOPER_DIR"] = developerRoot,
                },
                Timeout: TimeSpan.FromMinutes(5)),
            cancellationToken).ConfigureAwait(false);
        Match match = XcodeVersionRegex().Match(output.StandardOutput);
        if (!match.Success)
        {
            throw new FormatException(
                $"Could not parse the Xcode version for '{developerRoot}': "
                + output.StandardOutput.Trim());
        }

        string actualVersion = match.Groups["version"].Value;
        if (!string.Equals(actualVersion, version, StringComparison.Ordinal)
            && !actualVersion.StartsWith(version + ".", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Xcode at '{developerRoot}' is version '{actualVersion}'; "
                + $"expected {version}.x.");
        }

        return ProvisioningResult.ForInstallation(new InstallationManifest
        {
            Id = id,
            Kind = InstallationKind.Xcode,
            RootPath = developerRoot,
            Version = actualVersion,
            Environment = new Dictionary<string, string?>
            {
                ["DEVELOPER_DIR"] = developerRoot,
            },
            SourceUri = null,
            Sha256 = null,
            Revision = null,
        });
    }

    [GeneratedRegex(@"^Xcode\s+(?<version>\d+(?:\.\d+){1,2})", RegexOptions.Multiline)]
    private static partial Regex XcodeVersionRegex();
}

internal sealed class MacLlvmInventoryComponent(string id, int major)
    : ISetupComponent
{
    public string Id => id;

    public string Name => $"Homebrew LLVM {major} inventory";

    public IReadOnlyList<string> Dependencies => [];

    public async Task<ProvisioningResult> ProvisionAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        LocatedProgram clang = await ProgramLocator.ResolveCompilerAsync(
            context,
            [
                $"/opt/homebrew/opt/llvm@{major}/bin/clang",
                "/opt/homebrew/opt/llvm/bin/clang",
                $"clang-{major}",
            ],
            major,
            "Homebrew LLVM",
            cancellationToken).ConfigureAwait(false);
        string directory = Path.GetDirectoryName(clang.Path)
            ?? throw new InvalidDataException($"LLVM path '{clang.Path}' has no directory.");
        string clangxx = SetupPathGuard.RequireFile(
            Path.Combine(directory, "clang++"),
            $"Homebrew clang++ {major}");
        string clangxxVersion = await ProgramLocator.GetVersionAsync(
            context, clangxx, "clang", cancellationToken).ConfigureAwait(false);
        HostToolchainUtilities.RequireMajor(clangxx, clangxxVersion, major);
        return ProvisioningResult.ForInstallation(new InstallationManifest
        {
            Id = id,
            Kind = InstallationKind.Llvm,
            RootPath = clang.Path,
            Version = clang.Version,
            Environment = new Dictionary<string, string?>
            {
                ["CC"] = clang.Path,
                ["CXX"] = clangxx,
                ["PATH"] = HostToolchainUtilities.PrependPath(directory),
            },
            SourceUri = null,
            Sha256 = null,
            Revision = null,
        });
    }
}
