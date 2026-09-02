using Incant.Base;

namespace Incant.Core.Toolchains;

/// <summary>Discovers WASI SDK installations.</summary>
public sealed class WasiSdkDiscoveryProvider : IDiscoveryProvider
{
    private static readonly IReadOnlyCollection<Kind> s_kinds = Array.AsReadOnly(
        [Kind.WasiSdk]);

    /// <inheritdoc />
    public string Name => "WASI SDK";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds => s_kinds;

    /// <inheritdoc />
    public async ValueTask<DiscoveryResult> DiscoverAsync(
        DiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var candidates = new List<Candidate>();
        var seenPaths = new HashSet<string>(ProviderUtilities.GetPathComparer());
        foreach (PathHint hint in context.GetExplicitPaths(Kind.WasiSdk))
        {
            AddRootCandidate(candidates, seenPaths, hint.Path, Source.Explicit);
        }

        AddRootCandidate(
            candidates,
            seenPaths,
            context.GetEnvironmentVariable("WASI_SDK_PATH"),
            Source.Environment);
        if (Directory.Exists("/opt/wasi-sdk"))
        {
            AddRootCandidate(candidates, seenPaths, "/opt/wasi-sdk", Source.StandardPath);
        }

        foreach (string directory in ProviderUtilities.GetPathDirectories(context))
        {
            string compiler = Path.Combine(directory, ProviderUtilities.GetExecutableName("clang"));
            string? root = Directory.GetParent(directory)?.FullName;
            if (File.Exists(compiler)
                && root is not null
                && Directory.Exists(Path.Combine(root, "share", "wasi-sysroot")))
            {
                AddRootCandidate(
                    candidates,
                    seenPaths,
                    root,
                    Source.Path);
            }
        }

        var toolchains = new List<Installation>();
        var sdks = new List<SdkInstallation>();
        var diagnostics = new List<Diagnostic>();
        foreach (Candidate candidate in candidates)
        {
            (Installation? toolchain, SdkInstallation? sdk) = await InspectAsync(
                candidate,
                context,
                cancellationToken).ConfigureAwait(false);
            if (toolchain is null || sdk is null)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "invalid-candidate",
                    Name,
                    "The candidate does not contain Clang and a WASI sysroot.",
                    candidate.Path));
                continue;
            }

            toolchains.Add(toolchain);
            sdks.Add(sdk);
        }

        return new DiscoveryResult(toolchains, sdks, diagnostics);
    }

    private static async Task<(Installation? Installation, SdkInstallation? Sdk)> InspectAsync(
        Candidate candidate,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        string bin = Path.Combine(candidate.Path, "bin");
        string compiler = Path.Combine(bin, ProviderUtilities.GetExecutableName("clang"));
        string cppCompiler = Path.Combine(bin, ProviderUtilities.GetExecutableName("clang++"));
        string archiver = Path.Combine(bin, ProviderUtilities.GetExecutableName("llvm-ar"));
        string linker = Path.Combine(bin, ProviderUtilities.GetExecutableName("wasm-ld"));
        string sysroot = Path.Combine(candidate.Path, "share", "wasi-sysroot");
        if (!File.Exists(compiler)
            || !File.Exists(cppCompiler)
            || !File.Exists(archiver)
            || !File.Exists(linker)
            || !Directory.Exists(sysroot))
        {
            return (null, null);
        }

        ProcessResult? versionResult = await ProviderUtilities.TryRunProbeAsync(
            compiler,
            ["--version"],
            context,
            cancellationToken).ConfigureAwait(false);
        if (versionResult is null)
        {
            return (null, null);
        }

        Version? compilerVersion = ProviderUtilities.ParseVersion(versionResult.StandardOutput);
        Version? productVersion = TryReadProductVersion(candidate.Path) ?? compilerVersion;
        var components = new Component[]
        {
            new(ComponentKind.Compiler, compiler, context.HostArchitecture, TargetArchitecture.Wasm32),
            new(ComponentKind.CppCompiler, cppCompiler, context.HostArchitecture, TargetArchitecture.Wasm32),
            new(ComponentKind.Archiver, archiver, context.HostArchitecture, TargetArchitecture.Wasm32),
            new(ComponentKind.Linker, linker, context.HostArchitecture, TargetArchitecture.Wasm32),
            new(ComponentKind.Sysroot, sysroot, context.HostArchitecture, TargetArchitecture.Wasm32),
        };
        var toolchain = new Installation(
            Kind.WasiSdk,
            CompilerFamily.Clang,
            candidate.Path,
            context.HostOS,
            context.HostArchitecture,
            productVersion,
            compilerVersion,
            ProviderUtilities.GetChannel(candidate.Path, versionResult.StandardOutput),
            candidate.Sources,
            [TargetPlatform.Wasi],
            [TargetArchitecture.Wasm32],
            components,
            "wasm32-wasi");
        var sdk = new SdkInstallation(
            Kind.WasiSdk,
            TargetPlatform.Wasi,
            candidate.Path,
            sysroot,
            productVersion,
            candidate.Sources,
            [TargetArchitecture.Wasm32]);
        return (toolchain, sdk);
    }

    private static Version? TryReadProductVersion(string root)
    {
        foreach (string name in new[] { "VERSION", "VERSION.txt" })
        {
            string path = Path.Combine(root, name);
            try
            {
                if (File.Exists(path))
                {
                    return ProviderUtilities.ParseVersion(File.ReadAllText(path));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return ProviderUtilities.ParseVersion(Path.GetFileName(root));
    }

    private static void AddRootCandidate(
        ICollection<Candidate> candidates,
        ISet<string> seenPaths,
        string? path,
        Source source)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized = File.Exists(path)
            ? Directory.GetParent(Path.GetDirectoryName(path)!)?.FullName ?? path
            : path;
        ProviderUtilities.AddCandidate(candidates, seenPaths, normalized, source);
    }
}
