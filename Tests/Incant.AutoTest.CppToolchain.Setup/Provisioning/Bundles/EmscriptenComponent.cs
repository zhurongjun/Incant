using System.Text.Json;

namespace Incant.AutoTest.CppToolchain.Setup;

internal sealed class EmscriptenComponent(EmscriptenRelease release) : ISetupComponent
{
    private const string LayoutSeedVersion = "1";

    private static readonly IReadOnlyList<InstallationProbe> s_completionProbes =
    [
        new(Path.Combine("upstream", "emscripten", "emscripten-version.txt"), ProbeKind.File),
        new(".emscripten", ProbeKind.File),
        new(
            Path.Combine(
                "upstream",
                "emscripten",
                "cache",
                "sysroot",
                "lib",
                "wasm32-emscripten",
                "libc.a"),
            ProbeKind.File),
        new(
            Path.Combine(
                "upstream",
                "emscripten",
                "cache",
                "sysroot",
                "lib",
                "wasm32-emscripten",
                "pic",
                "libc.a"),
            ProbeKind.File),
    ];

    public string Id => $"emscripten-{release.Version}";

    public string Name => $"Emscripten {release.Version}";

    public IReadOnlyList<string> Dependencies => [];

    public async Task<ProvisioningResult> ProvisionAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        string emsdkRoot = context.Paths.AssertChild(
            Path.Combine(context.Options.ToolchainRoot, $"emsdk-{release.Version}"));
        string bootstrapPython = ProgramLocator.RequireCommand(
            OperatingSystem.IsWindows() ? ["python.exe", "python"] : ["python3", "python"],
            "Python for emsdk");
        EmscriptenHostPackage host = BundleCatalog.EmscriptenHost;
        string releaseUri =
            $"{BundleCatalog.EmscriptenPackageRoot}/{host.ReleasePlatform}/"
            + $"{release.ReleaseRevision}/{host.ReleaseFile}";
        string dependencyRoot = $"{BundleCatalog.EmscriptenPackageRoot}/deps";
        string nodeUri = $"{dependencyRoot}/{host.NodeFile}";
        string? pythonUri = host.PythonFile is null
            ? null
            : $"{dependencyRoot}/{host.PythonFile}";
        string fingerprint =
            $"emsdk={BundleCatalog.EmsdkRevision};emscripten={release.ReleaseRevision};"
            + $"archive={release.Sha256};node={host.NodeSha256};"
            + $"python={host.PythonSha256 ?? "system"};"
            + $"layoutSeed={LayoutSeedVersion}";

        if (await ComponentCompletionStore.IsReadyAsync(
            emsdkRoot,
            Id,
            fingerprint,
            s_completionProbes,
            cancellationToken).ConfigureAwait(false))
        {
            try
            {
                EmscriptenInstallation completed =
                    await EmscriptenRuntimeResolver.ResolveAsync(
                        context,
                        emsdkRoot,
                        release.Version,
                        bootstrapPython,
                        host,
                        cancellationToken).ConfigureAwait(false);
                Console.WriteLine(
                    $"[emscripten:cache-hit] root={emsdkRoot} version={release.Version}");
                return CreateResult(completed, releaseUri, nodeUri, pythonUri, host);
            }
            catch (Exception exception) when (exception is SetupCommandException
                or IOException
                or UnauthorizedAccessException
                or FormatException
                or ArgumentException
                or NotSupportedException)
            {
                Console.Error.WriteLine(
                    $"[emscripten:cache-invalid] root={emsdkRoot} reason={exception.Message}");
            }
        }

        string staging = context.Paths.AssertChild(
            emsdkRoot + $".provisioning.{Guid.NewGuid():N}");
        try
        {
            string git = ProgramLocator.RequireCommand(["git"], "git");
            await PrepareCheckoutAsync(
                context, staging, git, cancellationToken).ConfigureAwait(false);
            await VerifyReleaseTagAsync(context, staging, git, cancellationToken)
                .ConfigureAwait(false);

            string releaseArchive = await context.Downloads.GetAsync(
                releaseUri,
                release.Sha256,
                $"emscripten-{release.Version}-{host.Key}-{host.ReleaseFile}",
                cancellationToken).ConfigureAwait(false);
            string downloads = Path.Combine(staging, "downloads");
            await DownloadCache.CopyAtomicallyAsync(
                releaseArchive,
                Path.Combine(downloads, $"{release.ReleaseRevision}-{host.ReleaseFile}"),
                cancellationToken).ConfigureAwait(false);

            string nodeArchive = await context.Downloads.GetAsync(
                nodeUri,
                host.NodeSha256,
                cancellationToken).ConfigureAwait(false);
            await DownloadCache.CopyAtomicallyAsync(
                nodeArchive,
                Path.Combine(downloads, host.NodeFile),
                cancellationToken).ConfigureAwait(false);

            if (pythonUri is not null
                && host.PythonFile is not null
                && host.PythonSha256 is not null)
            {
                string pythonArchive = await context.Downloads.GetAsync(
                    pythonUri,
                    host.PythonSha256,
                    cancellationToken).ConfigureAwait(false);
                await DownloadCache.CopyAtomicallyAsync(
                    pythonArchive,
                    Path.Combine(downloads, host.PythonFile),
                    cancellationToken).ConfigureAwait(false);
            }

            string emsdkProgram = SetupPathGuard.RequireFile(
                Path.Combine(staging, "emsdk.py"),
                "emsdk Python entry point");
            var installEnvironment = new Dictionary<string, string?>
            {
                ["EMSDK_KEEP_DOWNLOADS"] = null,
            };
            await context.Commands.RunAsync(
                bootstrapPython,
                [emsdkProgram, "install", release.Version],
                new SetupCommandOptions(
                    WorkingDirectory: staging,
                    Environment: installEnvironment,
                    Timeout: TimeSpan.FromMinutes(45)),
                cancellationToken).ConfigureAwait(false);
            await context.Commands.RunAsync(
                bootstrapPython,
                [emsdkProgram, "activate", release.Version, "--embedded"],
                new SetupCommandOptions(
                    WorkingDirectory: staging,
                    Timeout: TimeSpan.FromMinutes(20)),
                cancellationToken).ConfigureAwait(false);

            EmscriptenInstallation installation =
                await EmscriptenRuntimeResolver.ResolveAsync(
                    context,
                    staging,
                    release.Version,
                    bootstrapPython,
                    host,
                    cancellationToken).ConfigureAwait(false);
            string embuilder = SetupPathGuard.RequireFile(
                Path.Combine(installation.RootPath, "embuilder.py"),
                "Emscripten system library builder");
            await context.Commands.RunAsync(
                installation.PythonPath,
                [embuilder, "build", "sysroot", "libc"],
                new SetupCommandOptions(
                    WorkingDirectory: installation.RootPath,
                    Environment: installation.Environment,
                    Timeout: TimeSpan.FromMinutes(45)),
                cancellationToken).ConfigureAwait(false);
            await context.Commands.RunAsync(
                installation.PythonPath,
                [embuilder, "--pic", "build", "libc"],
                new SetupCommandOptions(
                    WorkingDirectory: installation.RootPath,
                    Environment: installation.Environment,
                    Timeout: TimeSpan.FromMinutes(45)),
                cancellationToken).ConfigureAwait(false);

            if (!ComponentCompletionStore.ProbesExist(staging, s_completionProbes))
            {
                throw new InvalidDataException(
                    $"Emscripten {release.Version} did not produce "
                    + "the default and PIC system libraries.");
            }

            context.Paths.ReplaceDirectory(staging, emsdkRoot);
            string publishedEmsdk = SetupPathGuard.RequireFile(
                Path.Combine(emsdkRoot, "emsdk.py"),
                "published emsdk Python entry point");
            await context.Commands.RunAsync(
                bootstrapPython,
                [publishedEmsdk, "activate", release.Version, "--embedded"],
                new SetupCommandOptions(
                    WorkingDirectory: emsdkRoot,
                    Timeout: TimeSpan.FromMinutes(20)),
                cancellationToken).ConfigureAwait(false);
            EmscriptenInstallation published =
                await EmscriptenRuntimeResolver.ResolveAsync(
                    context,
                    emsdkRoot,
                    release.Version,
                    bootstrapPython,
                    host,
                    cancellationToken).ConfigureAwait(false);
            await ComponentCompletionStore.WriteAsync(
                emsdkRoot,
                Id,
                fingerprint,
                s_completionProbes,
                cancellationToken).ConfigureAwait(false);
            return CreateResult(published, releaseUri, nodeUri, pythonUri, host);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                context.Paths.DeleteDirectory(staging);
            }
        }
    }

    private async Task PrepareCheckoutAsync(
        SetupContext context,
        string emsdkRoot,
        string git,
        CancellationToken cancellationToken)
    {
        context.Paths.ResetDirectory(emsdkRoot);
        await context.Commands.RunAsync(
            git,
            ["init", emsdkRoot],
            cancellationToken).ConfigureAwait(false);
        await context.Commands.RunAsync(
            git,
            ["-C", emsdkRoot, "remote", "add", "origin", BundleCatalog.EmsdkUri],
            cancellationToken).ConfigureAwait(false);
        await context.Commands.RunAsync(
            git,
            ["-C", emsdkRoot, "fetch", "--depth", "1", "origin", BundleCatalog.EmsdkRevision],
            new SetupCommandOptions(
                Timeout: TimeSpan.FromMinutes(10),
                Attempts: 3,
                RetryDelay: TimeSpan.FromSeconds(2)),
            cancellationToken).ConfigureAwait(false);
        await context.Commands.RunAsync(
            git,
            ["-C", emsdkRoot, "checkout", "--detach", "--force", BundleCatalog.EmsdkRevision],
            new SetupCommandOptions(Timeout: TimeSpan.FromMinutes(5)),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyReleaseTagAsync(
        SetupContext context,
        string emsdkRoot,
        string git,
        CancellationToken cancellationToken)
    {
        SetupCommandOutput checkout = await context.Commands.RunAsync(
            git,
            ["-C", emsdkRoot, "rev-parse", "HEAD"],
            cancellationToken).ConfigureAwait(false);
        string actualRevision = checkout.StandardOutput.Trim();
        if (!string.Equals(
            actualRevision,
            BundleCatalog.EmsdkRevision,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"emsdk at '{emsdkRoot}' is revision '{actualRevision}'; "
                + $"expected '{BundleCatalog.EmsdkRevision}'.");
        }

        string releaseTagsPath = SetupPathGuard.RequireFile(
            Path.Combine(emsdkRoot, "emscripten-releases-tags.json"),
            "emsdk release manifest");
        await using FileStream stream = File.OpenRead(releaseTagsPath);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("releases", out JsonElement releases)
            || !releases.TryGetProperty(release.Version, out JsonElement revision))
        {
            throw new InvalidDataException(
                $"Emscripten {release.Version} is absent from emsdk revision "
                + $"{BundleCatalog.EmsdkRevision}.");
        }

        string? actualReleaseRevision = revision.GetString();
        if (!string.Equals(
            actualReleaseRevision,
            release.ReleaseRevision,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Emscripten {release.Version} resolves to {actualReleaseRevision} "
                + $"at emsdk revision {BundleCatalog.EmsdkRevision}; "
                + $"expected {release.ReleaseRevision}.");
        }
    }

    private ProvisioningResult CreateResult(
        EmscriptenInstallation installation,
        string releaseUri,
        string nodeUri,
        string? pythonUri,
        EmscriptenHostPackage host)
    {
        var manifest = new InstallationManifest
        {
            Id = Id,
            Kind = InstallationKind.Emscripten,
            RootPath = installation.RootPath,
            Version = release.Version,
            Environment = installation.Environment,
            SourceUri = releaseUri,
            Sha256 = release.Sha256,
            Revision =
                $"emsdk={BundleCatalog.EmsdkRevision}; emscripten={release.ReleaseRevision}",
        };
        var node = new RuntimeManifest
        {
            Id = $"node-{release.Version}",
            Kind = RuntimeKind.Node,
            Path = installation.NodePath,
            Version = installation.NodeVersion,
            InstallationId = Id,
            SourceUri = nodeUri,
            Sha256 = host.NodeSha256,
            Revision = "node-v24.19.0",
        };
        var python = new RuntimeManifest
        {
            Id = $"python-{release.Version}",
            Kind = RuntimeKind.Python,
            Path = installation.PythonPath,
            Version = installation.PythonVersion,
            InstallationId = Id,
            SourceUri = pythonUri,
            Sha256 = host.PythonSha256,
            Revision = host.PythonFile is null
                ? $"system-python-{installation.PythonVersion}"
                : "python-3.13.3",
        };
        return new ProvisioningResult([manifest], [node, python]);
    }
}
