using Incant.Base;
using Incant.CX;

namespace Incant.CX.FindTools;

/// <summary>Finds Android NDK, Emscripten and WASI compiler environments without eagerly checking sibling tools.</summary>
public sealed class BundleProvider : IDiscoveryProvider
{
    /// <inheritdoc />
    public string Name => "Android/WebAssembly";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds { get; } = Array.AsReadOnly(new[] { Kind.AndroidNdk, Kind.Emscripten, Kind.WasiSdk });

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(ToolSetQuery query, DiscoveryContext context, CancellationToken cancellationToken)
    {
        DiscoveryResult[] results = await Task.WhenAll(Kinds.Where(kind => query.Kind is null || query.Kind == kind)
            .Select(kind => DiscoverKindAsync(kind, query, context, cancellationToken))).ConfigureAwait(false);
        return new DiscoveryResult(results.SelectMany(result => result.ToolSets), results.SelectMany(result => result.Diagnostics));
    }

    private async Task<DiscoveryResult> DiscoverKindAsync(
        Kind kind, ToolSetQuery query, DiscoveryContext context, CancellationToken cancellationToken)
    {
        BundleKind bundleKind = kind switch
        {
            Kind.AndroidNdk => BundleKind.AndroidNdk,
            Kind.Emscripten => BundleKind.Emscripten,
            _ => BundleKind.WasiSdk,
        };
        BundleDiscoveryResult found = await BundleLocator.FindAsync(bundleKind, query.RootPath, context, cancellationToken).ConfigureAwait(false);
        DiscoveryResult[] results = await Task.WhenAll(
            found.Installations.Select(installation => InspectAsync(
                kind, installation, context, cancellationToken))).ConfigureAwait(false);
        return new DiscoveryResult(results.SelectMany(result => result.ToolSets),
            found.Diagnostics.Concat(results.SelectMany(result => result.Diagnostics)));
    }

    private async Task<DiscoveryResult> InspectAsync(
        Kind kind, BundleInstallation installation, DiscoveryContext context, CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        if (installation.Compiler is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "missing-compiler", Name,
                "The bundle identity is known, but the primary compiler is missing.", installation.Root));
        }

        // Emscripten wrappers can initialize their cache even for probes; use installed metadata instead.
        string? identityCompiler = kind == Kind.Emscripten
            ? SearchPaths.Executable(Path.GetFullPath(Path.Combine(installation.Root, "..", "bin")), "clang")
            : installation.Compiler;
        ProcessResult? identity = identityCompiler is null ? null
            : await context.ProbeAsync(identityCompiler, ["--version"], cancellationToken).ConfigureAwait(false);
        Version? compilerVersion = identity is null ? null : SearchPaths.CompilerVersion(identity.StandardOutput);
        if (kind != Kind.Emscripten && identity is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "compiler-probe-failed", Name,
                "The bundled compiler did not respond to its version query.", installation.Root));
        }

        string? triple = kind switch
        {
            Kind.Emscripten => "wasm32-unknown-emscripten",
            Kind.WasiSdk => installation.TargetTriple,
            _ => null,
        };
        return new DiscoveryResult([new DirectoryToolSet(kind, installation.Root, installation.Root,
            installation.Bin, installation.Version, installation.Version, compilerVersion,
            installation.Compiler, triple, installation.Channel,
            installation.Candidate.Sources, context,
            kind == Kind.Emscripten, diagnostics)]);
    }
}
