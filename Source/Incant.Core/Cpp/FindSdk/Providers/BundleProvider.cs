using System.Text.Json;
using Incant.Core.Cpp;

namespace Incant.Core.Cpp.FindSdk;

/// <summary>Reports installed Android and WebAssembly platform files independently of bundled compiler resources.</summary>
public sealed class BundleProvider : IDiscoveryProvider
{
    /// <inheritdoc />
    public string Name => "Android/WebAssembly SDK";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds { get; } = Array.AsReadOnly(new[] { Kind.AndroidNdk, Kind.Emscripten, Kind.WasiSdk });

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(SdkQuery query, DiscoveryContext context, CancellationToken cancellationToken)
    {
        DiscoveryResult[] results = await Task.WhenAll(Kinds.Where(kind => query.Kind is null || query.Kind == kind)
            .Select(kind => DiscoverKindAsync(kind, query, context, cancellationToken))).ConfigureAwait(false);
        return new DiscoveryResult(results.SelectMany(result => result.Sdks), results.SelectMany(result => result.Diagnostics));
    }

    private async Task<DiscoveryResult> DiscoverKindAsync(Kind kind, SdkQuery query, DiscoveryContext context, CancellationToken cancellationToken)
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
                kind, installation, cancellationToken))).ConfigureAwait(false);
        return new DiscoveryResult(results.SelectMany(result => result.Sdks),
            found.Diagnostics.Concat(results.SelectMany(result => result.Diagnostics)));
    }

    private async Task<DiscoveryResult> InspectAsync(Kind kind, BundleInstallation installation, CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        var layouts = new List<TargetLayout>();
        try
        {
            if (!Directory.Exists(installation.Sysroot))
            {
                diagnostics.Add(Resources.Missing(Name, "The installed sysroot is absent; discovery does not populate caches.", installation.Sysroot));
            }
            else if (kind == Kind.AndroidNdk)
            {
                layouts.AddRange(await AndroidLayoutsAsync(installation, cancellationToken).ConfigureAwait(false));
            }
            else if (kind == Kind.Emscripten)
            {
                layouts.AddRange(EmscriptenLayouts(installation, cancellationToken));
            }
            else
            {
                string targetTriple = installation.TargetTriple
                    ?? WasiTargetResolver.Preview1Triple;
                layouts.AddRange(WasiResourceLayout.Create(installation.Sysroot, targetTriple, cancellationToken));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostics.Add(Resources.Missing(Name, exception.Message, installation.Root));
        }

        if (layouts.Count == 0)
        {
            diagnostics.Add(Resources.Missing(Name, "No installed target resource layout could be established.", installation.Root));
        }

        return new DiscoveryResult([new Sdk(kind, installation.Root, layouts, installation.Version,
            channel: installation.Channel, sources: installation.Candidate.Sources, diagnostics: diagnostics)]);
    }

    private IEnumerable<TargetLayout> EmscriptenLayouts(BundleInstallation installation, CancellationToken cancellationToken)
    {
        Resource[] headers = Resources.Sysroot(installation.Sysroot).Where(resource =>
            resource.Purpose is ResourcePurpose.CInclude or ResourcePurpose.CppInclude or ResourcePurpose.Framework).ToArray();
        string libraryRoot = Path.Combine(installation.Sysroot, "lib", "wasm32-emscripten");
        if (!Directory.Exists(libraryRoot))
        {
            libraryRoot = Path.Combine(installation.Sysroot, "usr", "lib", "wasm32-emscripten");
        }

        var pending = new Queue<string>();
        pending.Enqueue(libraryRoot);
        while (pending.TryDequeue(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var collector = new ResourceCollector(installation.Sysroot);
            collector.AddRange(headers);
            Resources.Libraries(collector, directory);
            IReadOnlyList<Resource> resources = collector.Build();
            string variant = Path.GetRelativePath(libraryRoot, directory).Replace(Path.DirectorySeparatorChar, '/');
            if (variant == "." || resources.Any(resource => resource.Purpose == ResourcePurpose.Library))
            {
                yield return new TargetLayout(TargetPlatform.Emscripten, TargetArchitecture.Wasm32, resources,
                    "wasm32-unknown-emscripten", installation.Sysroot, multilib: variant,
                    diagnostics: MissingGroups(resources, directory));
            }

            foreach (string child in SearchPaths.Directories(directory).Order(SearchPaths.Comparer))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Enqueue(child);
                }
            }
        }
    }

    private async Task<IReadOnlyList<TargetLayout>> AndroidLayoutsAsync(BundleInstallation installation, CancellationToken cancellationToken)
    {
        var aliases = new Dictionary<int, int>();
        var metadataDiagnostics = new List<Diagnostic>();
        try
        {
            string? metadata = await SearchPaths.ReadTextAsync(Path.Combine(installation.Root, "meta", "platforms.json"), cancellationToken).ConfigureAwait(false);
            if (metadata is not null)
            {
                using JsonDocument document = JsonDocument.Parse(metadata);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("NDK platform metadata must be an object.");
                }

                if (document.RootElement.TryGetProperty("aliases", out JsonElement values))
                {
                    if (values.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException("NDK platform aliases must be an object.");
                    }

                    foreach (JsonProperty alias in values.EnumerateObject())
                    {
                        if (int.TryParse(alias.Name, out int from) && from > 0 && alias.Value.ValueKind == JsonValueKind.Number && alias.Value.TryGetInt32(out int to) && to > 0)
                        {
                            aliases[from] = to;
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            metadataDiagnostics.Add(Resources.Missing(Name, exception.Message, installation.Root));
        }

        var layouts = new List<TargetLayout>();
        foreach ((string triple, TargetArchitecture architecture) in new[]
        {
            ("i686-linux-android", TargetArchitecture.X86), ("x86_64-linux-android", TargetArchitecture.X64),
            ("arm-linux-androideabi", TargetArchitecture.ARM), ("aarch64-linux-android", TargetArchitecture.ARM64),
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            string libraryRoot = Path.Combine(installation.Sysroot, "usr", "lib", triple);
            if (!Directory.Exists(libraryRoot) && !Directory.Exists(Path.Combine(installation.Sysroot, "usr", "include", triple)))
            {
                continue;
            }

            var diagnostics = new List<Diagnostic>(metadataDiagnostics);
            int[] levels = SearchPaths.Directories(libraryRoot).Select(Path.GetFileName)
                .Select(name => int.TryParse(name, out int level) ? level : 0).Where(level => level > 0).Order().ToArray();
            var collector = new ResourceCollector(installation.Sysroot);
            collector.AddRange(Resources.Sysroot(installation.Sysroot, triple));
            foreach (int level in levels)
            {
                Resources.Libraries(collector, Path.Combine(libraryRoot, level.ToString(System.Globalization.CultureInfo.InvariantCulture)), level);
            }

            IReadOnlyList<Resource> resources = collector.Build();
            if (levels.Length == 0)
            {
                diagnostics.Add(Resources.Missing(Name, "No installed API library levels were found for this ABI.", libraryRoot));
            }

            diagnostics.AddRange(MissingGroups(resources, libraryRoot));
            Dictionary<int, int> availableAliases = aliases.Where(alias => levels.Contains(alias.Value))
                .ToDictionary(alias => alias.Key, alias => alias.Value);
            layouts.Add(new TargetLayout(TargetPlatform.Android, architecture, resources,
                architecture == TargetArchitecture.ARM ? "armv7a-linux-androideabi" : triple,
                installation.Sysroot, levels, availableAliases, diagnostics: diagnostics));
        }

        return layouts;
    }

    private IEnumerable<Diagnostic> MissingGroups(IReadOnlyList<Resource> resources, string path)
    {
        if (!resources.Any(resource => resource.Purpose == ResourcePurpose.CInclude))
        {
            yield return Resources.Missing(Name, "Platform development headers are missing.", path);
        }

        if (!resources.Any(resource => resource.Purpose == ResourcePurpose.Library))
        {
            yield return Resources.Missing(Name, "Target platform libraries are missing.", path);
        }
    }
}
