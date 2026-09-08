namespace Incant.AutoTest.CXToolchain.Setup;

internal sealed class WasmtimeComponent(WasmtimeRelease release) : ISetupComponent
{
    public string Id => $"wasmtime-{release.Version}";

    public string Name => $"Wasmtime {release.Version}";

    public IReadOnlyList<string> Dependencies => [];

    public async Task<ProvisioningResult> ProvisionAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        string uri =
            $"https://github.com/bytecodealliance/wasmtime/releases/download/v{release.Version}"
            + $"/wasmtime-v{release.Version}-{release.Platform}.{release.Extension}";
        string archive = await context.Downloads.GetAsync(
            uri, release.Sha256, cancellationToken).ConfigureAwait(false);
        string root = await context.Archives.InstallAsync(
            Id,
            archive,
            Path.Combine(context.Options.ToolchainRoot, Id),
            [new(release.Executable, ProbeKind.File)],
            release.Sha256,
            cancellationToken).ConfigureAwait(false);
        string runtime = SetupPathGuard.RequireFile(
            Path.Combine(root, release.Executable),
            $"Wasmtime {release.Version}");
        string actualVersion = await ProgramLocator.GetVersionAsync(
            context,
            runtime,
            "wasmtime",
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualVersion, release.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Wasmtime at '{runtime}' is version '{actualVersion}'; "
                + $"expected '{release.Version}'.");
        }

        return ProvisioningResult.ForRuntime(new RuntimeManifest
        {
            Id = Id,
            Kind = RuntimeKind.Wasmtime,
            Path = runtime,
            Version = actualVersion,
            InstallationId = null,
            SourceUri = uri,
            Sha256 = release.Sha256,
            Revision = $"v{release.Version}",
        });
    }
}
