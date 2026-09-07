namespace Incant.AutoTest.CppToolchain.Setup;

internal sealed class WasiSdkComponent(WasiRelease release) : ISetupComponent
{
    public string Id => $"wasi-sdk-{release.Version}";

    public string Name => $"WASI SDK {release.Version}";

    public IReadOnlyList<string> Dependencies => [];

    public async Task<ProvisioningResult> ProvisionAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        string uri =
            $"https://github.com/WebAssembly/wasi-sdk/releases/download/wasi-sdk-{release.Version}"
            + $"/wasi-sdk-{release.Version}.0-{release.Platform}.tar.gz";
        string archive = await context.Downloads.GetAsync(
            uri, release.Sha256, cancellationToken).ConfigureAwait(false);
        string executableSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        InstallationProbe[] probes =
        [
            new(Path.Combine("bin", $"clang{executableSuffix}"), ProbeKind.File),
            new(Path.Combine("bin", $"clang++{executableSuffix}"), ProbeKind.File),
            new(Path.Combine("bin", $"llvm-ar{executableSuffix}"), ProbeKind.File),
            .. release.RequiredFiles.Select(relative => new InstallationProbe(
                Path.Combine("share", "wasi-sysroot", relative), ProbeKind.File)),
        ];
        string root = await context.Archives.InstallAsync(
            Id,
            archive,
            Path.Combine(context.Options.ToolchainRoot, Id),
            probes,
            release.Sha256,
            cancellationToken).ConfigureAwait(false);
        return ProvisioningResult.ForInstallation(new InstallationManifest
        {
            Id = Id,
            Kind = InstallationKind.WasiSdk,
            RootPath = root,
            Version = $"{release.Version}.0",
            Environment = new Dictionary<string, string?>
            {
                ["WASI_SDK_PATH"] = root,
            },
            SourceUri = uri,
            Sha256 = release.Sha256,
            Revision = Id,
        });
    }
}
