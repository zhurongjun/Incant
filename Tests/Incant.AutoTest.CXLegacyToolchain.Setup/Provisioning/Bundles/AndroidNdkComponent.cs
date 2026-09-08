using System.Text.RegularExpressions;

namespace Incant.AutoTest.CXLegacyToolchain.Setup;

internal sealed partial class AndroidNdkComponent(AndroidRelease release) : ISetupComponent
{
    public string Id => release.Id;

    public string Name => $"Android NDK {release.Version}";

    public IReadOnlyList<string> Dependencies => [];

    public async Task<ProvisioningResult> ProvisionAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        string platform = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "darwin"
                    : throw new PlatformNotSupportedException();
        string hostTag = OperatingSystem.IsWindows()
            ? "windows-x86_64"
            : OperatingSystem.IsLinux()
                ? "linux-x86_64"
                : "darwin-x86_64";
        string suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        string uri =
            $"https://dl.google.com/android/repository/android-ndk-{release.Release}-{platform}.zip";
        string archive = await context.Downloads.GetAsync(
            uri, release.Sha256, cancellationToken).ConfigureAwait(false);
        string bin = Path.Combine("toolchains", "llvm", "prebuilt", hostTag, "bin");
        string root = await context.Archives.InstallAsync(
            release.Id,
            archive,
            Path.Combine(context.Options.ToolchainRoot, $"android-ndk-{release.Version}"),
            [
                new("source.properties", ProbeKind.File),
                new(
                    Path.Combine(bin, $"clang{suffix}"),
                    ProbeKind.Executable,
                    ["--version"]),
                new(
                    Path.Combine(bin, $"clang++{suffix}"),
                    ProbeKind.Executable,
                    ["--version"]),
                new(
                    Path.Combine(bin, $"llvm-ar{suffix}"),
                    ProbeKind.Executable,
                    ["--version"]),
                new(
                    Path.Combine(bin, $"llvm-ranlib{suffix}"),
                    ProbeKind.Executable,
                    ["--version"]),
                new(
                    Path.Combine(bin, $"ld.lld{suffix}"),
                    ProbeKind.Executable,
                    ["--version"]),
                new(
                    Path.Combine(
                        "toolchains",
                        "llvm",
                        "prebuilt",
                        hostTag,
                        "sysroot",
                        "usr",
                        "include",
                        "stdio.h"),
                    ProbeKind.File),
            ],
            release.Sha256,
            cancellationToken).ConfigureAwait(false);
        string metadata = await File.ReadAllTextAsync(
            Path.Combine(root, "source.properties"),
            cancellationToken).ConfigureAwait(false);
        Match revision = RevisionRegex().Match(metadata);
        if (!revision.Success
            || !string.Equals(
                revision.Groups["version"].Value,
                release.Version,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Android NDK archive '{uri}' reports revision "
                + $"'{(revision.Success ? revision.Groups["version"].Value : "missing")}'; "
                + $"expected '{release.Version}'.");
        }

        return ProvisioningResult.ForInstallation(new InstallationManifest
        {
            Id = release.Id,
            Kind = InstallationKind.AndroidNdk,
            RootPath = root,
            Version = release.Version,
            Environment = new Dictionary<string, string?>
            {
                ["ANDROID_NDK_HOME"] = root,
                ["ANDROID_NDK_ROOT"] = root,
            },
            SourceUri = uri,
            Sha256 = release.Sha256,
            Revision = release.Release,
        });
    }

    [GeneratedRegex(@"^\s*Pkg\.Revision\s*=\s*(?<version>\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex RevisionRegex();
}
