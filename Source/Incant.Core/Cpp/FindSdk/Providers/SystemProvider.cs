using Incant.Base;
using Incant.Core.Cpp;

namespace Incant.Core.Cpp.FindSdk;

/// <summary>Discovers Linux platform development files and isolated sysroots independently of compiler SDKs.</summary>
public sealed class SystemProvider : IDiscoveryProvider
{
    /// <inheritdoc />
    public string Name => "System/sysroot";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds { get; } = Array.AsReadOnly(new[] { Kind.Linux, Kind.Sysroot });

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(SdkQuery query, DiscoveryContext context, CancellationToken cancellationToken)
    {
        string? explicitRoot = query.SysrootPath ?? query.RootPath;
        if (explicitRoot is not null && !Directory.Exists(explicitRoot))
        {
            throw new DiscoveryException($"The explicit sysroot '{explicitRoot}' does not exist.");
        }
        if (query.CompilerPath is not null)
        {
            CompilerProbe? compiler = await CompilerProbe.OpenAsync(query.CompilerPath, context, cancellationToken).ConfigureAwait(false);
            if (compiler is null)
            {
                return Missing("The compiler identity query failed.", query.CompilerPath);
            }

            CompilerTargets result = await compiler.FindTargetsAsync(query with { SysrootPath = explicitRoot }, cancellationToken).ConfigureAwait(false);
            var sdks = new List<Sdk>();
            var diagnostics = new List<Diagnostic>(result.Diagnostics);
            foreach (CompilerTarget target in result.Targets)
            {
                string? root = target.Sysroot;
                bool isNativeRoot = root is null or "/";
                if (isNativeRoot && (context.HostOS != PlatformOS.Linux || target.Identity.Platform != TargetPlatform.Linux))
                {
                    diagnostics.Add(Resources.Missing(Name, "No target sysroot was established; host files were not substituted.", compiler.Path));
                    continue;
                }

                root ??= "/";
                if (!Directory.Exists(root))
                {
                    diagnostics.Add(Resources.Missing(Name, "The reported sysroot does not exist.", root));
                    continue;
                }

                if (isNativeRoot && query.Kind == Kind.Sysroot)
                {
                    continue;
                }

                TargetLayout layout = await CollectCompilerLayoutAsync(target, root, cancellationToken).ConfigureAwait(false);
                sdks.Add(new Sdk(isNativeRoot ? Kind.Linux : Kind.Sysroot, root, [layout], compilerPath: query.CompilerPath,
                    sources: [Source.Explicit]));
            }

            return new DiscoveryResult(sdks, diagnostics).WithRecognizedInputs(explicitRoot is null ? [query.CompilerPath] : [query.CompilerPath, explicitRoot]);
        }

        bool isNative = explicitRoot is null || explicitRoot == "/";
        if (isNative && (context.HostOS != PlatformOS.Linux || query.Kind == Kind.Sysroot))
        {
            return new DiscoveryResult();
        }

        string sysroot = explicitRoot ?? "/";
        if (!Directory.Exists(sysroot))
        {
            return Missing("The sysroot directory does not exist.", sysroot);
        }

        var targets = new Dictionary<string, TargetIdentity>(StringComparer.Ordinal);
        foreach (string relative in new[] { "usr/lib", "lib", "usr/include" })
        {
            foreach (string directory in SearchPaths.Directories(Path.Combine(sysroot, relative)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(directory);
                var target = new TargetIdentity(name);
                if (target.Platform == TargetPlatform.Linux && target.Architecture != TargetArchitecture.Unknown
                    && (query.TargetTriple is null || TargetIdentity.AreEquivalent(name, query.TargetTriple))
                    && (query.TargetArchitecture is null || query.TargetArchitecture == target.Architecture)
                    && (query.TargetPlatform is null || query.TargetPlatform == target.Platform)
                    && (!isNative || query.TargetArchitecture is not null || query.TargetTriple is not null || target.Architecture == context.HostArchitecture))
                {
                    targets.TryAdd(name, target);
                }
            }
        }

        var layouts = new List<TargetLayout>();
        foreach ((string directoryName, TargetIdentity target) in targets)
        {
            IReadOnlyList<Resource> resources = CollectStatic(sysroot, directoryName, target, isNative);
            layouts.Add(new TargetLayout(target.Platform, target.Architecture, resources, target.Triple, sysroot,
                diagnostics: MissingGroups(resources, sysroot)));
        }

        if (layouts.Count == 0)
        {
            bool hasUnconstrainedNativeTarget = isNative && query.TargetTriple is null
                && (query.TargetArchitecture is null || query.TargetArchitecture == context.HostArchitecture)
                && query.TargetPlatform is null or TargetPlatform.Linux;
            IReadOnlyList<Resource> resources = Resources.Sysroot(sysroot).Where(resource =>
                resource.Purpose is ResourcePurpose.CInclude or ResourcePurpose.CppInclude or ResourcePurpose.Framework
                || hasUnconstrainedNativeTarget).ToArray();
            layouts.Add(new TargetLayout(isNative ? TargetPlatform.Linux : TargetPlatform.Unknown,
                hasUnconstrainedNativeTarget ? context.HostArchitecture : TargetArchitecture.Unknown, resources, sysrootPath: sysroot,
                diagnostics: [Resources.Missing(Name, "A target ABI could not be established from installed layout metadata.", sysroot)]));
        }

        return new DiscoveryResult([new Sdk(isNative ? Kind.Linux : Kind.Sysroot, sysroot, layouts,
            sources: [explicitRoot is null ? Source.StandardPath : Source.Explicit])]);
    }

    private async Task<TargetLayout> CollectCompilerLayoutAsync(CompilerTarget target, string root, CancellationToken cancellationToken)
    {
        var collector = new ResourceCollector(root);
        var diagnostics = new List<Diagnostic>(target.Diagnostics);
        try
        {
            bool isNativeRoot = root == "/";
            bool hasForeignLibc = TargetResources.HasForeignLibc(target);
            string? libc = await target.FindFileAsync("libc.so", cancellationToken).ConfigureAwait(false)
                ?? await target.FindFileAsync("libc.a", cancellationToken).ConfigureAwait(false);
            bool hasLibcEvidence = libc is not null && TargetResources.IsCompatibleFile(libc, target, hasForeignLibc);
            foreach (CompilerInclude include in target.Includes)
            {
                if (SearchPaths.Contains(root, include.Path)
                    && (target.ResourceDirectory is null || !SearchPaths.Contains(target.ResourceDirectory, include.Path))
                    && !include.Path.Contains(Path.DirectorySeparatorChar + "c++" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !include.Path.Contains(Path.DirectorySeparatorChar + "gcc" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !TargetResources.HasConflictingDirectory(include.Path, target.Identity)
                    && (!hasForeignLibc || hasLibcEvidence && TargetResources.HasCompatibleDirectory(include.Path, target.Identity)))
                {
                    collector.Add(include.Purpose, include.Path);
                }
            }

            bool hasUnconfirmedFiles = false;
            foreach (string directory in target.LibraryDirectories)
            {
                if (!SearchPaths.Contains(root, directory) || TargetResources.HasConflictingDirectory(directory, target.Identity)
                    || directory.Contains(Path.DirectorySeparatorChar + "gcc" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || target.ResourceDirectory is not null && SearchPaths.Contains(target.ResourceDirectory, directory))
                {
                    continue;
                }

                bool isTargetDirectory = TargetResources.HasCompatibleDirectory(directory, target.Identity);
                bool hasSelectedLibc = hasLibcEvidence && libc is not null
                    && SearchPaths.Comparer.Equals(directory, Path.GetDirectoryName(libc));
                if (!isNativeRoot || isTargetDirectory || hasSelectedLibc)
                {
                    collector.Add(ResourcePurpose.LibraryDirectory, directory);
                    foreach (string file in SearchPaths.Files(directory).Where(TargetResources.IsLibrary))
                    {
                        if (TargetResources.IsCompatibleFile(file, target, hasForeignLibc))
                        {
                            collector.Add(ResourcePurpose.Library, file);
                        }
                        else
                        {
                            hasUnconfirmedFiles = true;
                        }
                    }
                }
            }

            foreach (string name in new[] { "libc.so", "libc.a", "crt1.o", "Scrt1.o", "rcrt1.o", "crti.o", "crtn.o" })
            {
                string? path = await target.FindFileAsync(name, cancellationToken).ConfigureAwait(false);
                if (path is not null && SearchPaths.Contains(root, path) && TargetResources.IsCompatibleFile(path, target, hasForeignLibc))
                {
                    collector.Add(TargetResources.IsStartup(path) ? ResourcePurpose.Startup : ResourcePurpose.Library, path);
                }
                else if (path is not null)
                {
                    hasUnconfirmedFiles = true;
                }
            }

            IReadOnlyList<Resource> resources = collector.Build();
            diagnostics.AddRange(MissingGroups(resources, root));
            if (hasUnconfirmedFiles)
            {
                diagnostics.Add(Resources.Missing(Name, "Some driver-reported files could not be confirmed for this target ABI and were excluded.", root));
            }
            if (!hasLibcEvidence)
            {
                diagnostics.Add(Resources.Missing(Name, "No compatible libc development library was confirmed for this target.", root));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Resources.Missing(Name, exception.Message, root));
        }

        return new TargetLayout(target.Identity.Platform, target.Identity.Architecture, collector.Build(), target.Identity.Triple, root,
            multilib: target.Multilib, diagnostics: diagnostics);
    }

    private static IReadOnlyList<Resource> CollectStatic(string root, string multiarch, TargetIdentity target, bool isNative)
    {
        var resources = new ResourceCollector(root);
        Resources.Headers(resources, Path.Combine(root, "usr", "include", multiarch));
        if (!isNative || TargetResources.SystemHeadersMatch(root, target) is true)
        {
            Resources.Headers(resources, Path.Combine(root, "usr", "include"));
            Resources.Headers(resources, Path.Combine(root, "include"));
        }
        foreach (string suffix in new[] { "usr/lib", "lib" })
        {
            Resources.Libraries(resources, Path.Combine(root, suffix, multiarch));
        }

        if (!isNative)
        {
            foreach (string suffix in new[] { "usr/lib", "lib", "usr/lib64", "lib64", "usr/lib32", "lib32", "usr/libx32", "libx32" })
            {
                string directory = Path.Combine(root, suffix);
                if (!TargetResources.HasConflictingDirectory(directory, target))
                {
                    Resources.Libraries(resources, directory);
                }
            }
        }

        return resources.Build();
    }

    private IEnumerable<Diagnostic> MissingGroups(IReadOnlyList<Resource> resources, string root)
    {
        if (!resources.Any(resource => resource.Purpose == ResourcePurpose.CInclude))
        {
            yield return Resources.Missing(Name, "No platform development headers were found.", root);
        }

        if (!resources.Any(resource => resource.Purpose == ResourcePurpose.Library))
        {
            yield return Resources.Missing(Name, "No target platform libraries were found.", root);
        }
    }

    private DiscoveryResult Missing(string message, string path) =>
        new(diagnostics: [Resources.Missing(Name, message, path)]);
}
