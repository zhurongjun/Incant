namespace Incant.CXLegacy.FindSdk;

/// <summary>Keeps WASI target resources and exception variants together without interpreting build flags.</summary>
internal sealed class WasiResourceLayout
{
    private static readonly IReadOnlyList<Variant> s_variants = Array.AsReadOnly(new[]
    {
        new Variant(".", "noeh"),
        new Variant("eh", "eh"),
    });

    private readonly TargetDirectories _target;

    private readonly Variant _variant;

    private readonly ResourceCollector _resources;

    private readonly List<Diagnostic> _diagnostics;

    private readonly CancellationToken _cancellationToken;

    private WasiResourceLayout(
        TargetDirectories target,
        Variant variant,
        IEnumerable<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        _target = target;
        _variant = variant;
        _resources = new ResourceCollector(target.Sysroot);
        _diagnostics = diagnostics.ToList();
        _cancellationToken = cancellationToken;
    }

    internal static IReadOnlyList<TargetLayout> Create(
        string sysroot,
        string targetTriple,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> triples = WasiTargetResolver.ResourceTripleCandidates(targetTriple);
        string usr = Path.Combine(sysroot, "usr");
        // Prefer a complete prefix, while retaining sysroots that separate their include and lib roots.
        (string Include, string Library)[] prefixes = [(sysroot, sysroot), (usr, usr), (sysroot, usr), (usr, sysroot)];
        TargetDirectories[] targets = triples.SelectMany(triple => prefixes.Select(prefix =>
            new TargetDirectories(sysroot, targetTriple, Path.Combine(prefix.Include, "include"),
                Path.Combine(prefix.Library, "lib"), triple))).ToArray();
        var diagnostics = new List<Diagnostic>();
        var groups = new List<(Variant Variant, TargetDirectories[] Targets)>();
        foreach (Variant variant in s_variants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TargetDirectories[] matching = targets.Where(target =>
                HasDirectoryEntry(Path.Combine(target.TargetInclude, variant.Directory!), diagnostics)
                || HasDirectoryEntry(Path.Combine(target.TargetLibrary, variant.Directory!), diagnostics)).ToArray();
            if (matching.Length > 0)
            {
                groups.Add((variant, matching));
            }
        }

        if (groups.Count == 0)
        {
            groups.Add((new Variant(null, null), targets));
        }

        return groups.Select(group => SelectLayout(group.Targets, group.Variant, diagnostics, cancellationToken)).ToArray();
    }

    private static TargetLayout SelectLayout(
        IReadOnlyList<TargetDirectories> targets,
        Variant variant,
        IReadOnlyList<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var candidates = new List<CollectedLayout>();
        foreach (TargetDirectories target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectedLayout candidate = new WasiResourceLayout(target, variant, diagnostics, cancellationToken).Collect();
            if (candidate.HasRequiredResources)
            {
                return candidate.Layout;
            }

            candidates.Add(candidate);
        }

        // Keep one target alias and one location per component. A union of candidates can hide
        // missing components and make libc++ include_next encounter another copy of its own wrapper.
        return (candidates.FirstOrDefault(candidate => candidate.HasFiles) ?? candidates[0]).Layout;
    }

    private CollectedLayout Collect()
    {
        string targetCppDirectory = Path.Combine(_target.TargetInclude, _variant.Directory ?? "", "c++", "v1");
        string[] cppDirectories = _variant.Directory is null
            ? [targetCppDirectory, Path.Combine(_target.IncludeRoot, "c++", "v1")]
            : [targetCppDirectory];
        string? cppDirectory = SelectCppDirectory(cppDirectories);
        if (cppDirectory is not null)
        {
            CollectDirectories([cppDirectory], path => _resources.Add(ResourcePurpose.CppInclude, path));
        }

        // C headers also serve C++, but cannot establish a C++ standard-library header group.
        bool hasCppHeaders = HasHeader(_resources.Build(), ResourcePurpose.CppInclude, "array");
        CollectDirectories([_target.TargetInclude, _target.IncludeRoot], path => Resources.Headers(_resources, path));
        if (_variant.Directory is not null)
        {
            CollectDirectories([Path.Combine(_target.TargetLibrary, _variant.Directory)],
                path => CollectLibraries(path, includeCppRuntime: true));
        }

        CollectDirectories([_target.TargetLibrary],
            path => CollectLibraries(path, includeCppRuntime: _variant.Directory is null));
        IReadOnlyList<Resource> resources = _resources.Build();
        bool hasCHeaders = HasHeader(resources, ResourcePurpose.CInclude, "stdio.h");
        if (!hasCHeaders)
        {
            Missing("C header", "stdio.h");
        }

        if (!hasCppHeaders)
        {
            Missing("C++ header", "array");
        }

        bool hasRequiredResources = hasCHeaders && hasCppHeaders;
        string[] requiredLibraries = _variant.Identifier == "eh"
            ? ["libc.a", "libc++.a", "libc++abi.a", "libunwind.a"]
            : ["libc.a", "libc++.a", "libc++abi.a"];
        foreach (string library in requiredLibraries)
        {
            if (!resources.Any(resource => resource.Purpose == ResourcePurpose.Library
                && Path.GetFileName(resource.Path) == library))
            {
                hasRequiredResources = false;
                Missing("library", library);
            }
        }

        bool hasFiles = resources.Any(resource => resource.Purpose is ResourcePurpose.Library or ResourcePurpose.Startup)
            || resources.Where(resource => resource.Purpose is ResourcePurpose.CInclude or ResourcePurpose.CppInclude)
                .Select(resource => resource.Path).Distinct(SearchPaths.Comparer).Any(HasFiles);
        var layout = new TargetLayout(TargetPlatform.Wasi, TargetArchitecture.Wasm32, resources,
            _target.Triple, _target.Sysroot, multilib: _variant.Identifier, diagnostics: _diagnostics.Distinct());
        return new CollectedLayout(layout, hasRequiredResources, hasFiles);
    }

    private string? SelectCppDirectory(IEnumerable<string> directories)
    {
        string? firstExisting = null;
        string? firstWithFiles = null;
        foreach (string directory in directories)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string path = SearchPaths.Normalize(directory);
                if (!Directory.Exists(path))
                {
                    continue;
                }

                if (File.Exists(Path.Combine(path, "array")))
                {
                    return path;
                }

                firstExisting ??= path;
                if (SearchPaths.Files(path).Any())
                {
                    firstWithFiles ??= path;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _diagnostics.Add(Resources.Missing("WASI SDK", exception.Message, directory));
            }
        }

        return firstWithFiles ?? firstExisting;
    }

    private bool HasFiles(string directory)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return SearchPaths.Files(directory).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _diagnostics.Add(Resources.Missing("WASI SDK", exception.Message, directory));
            return false;
        }
    }

    private void CollectDirectories(IEnumerable<string> directories, Action<string> collect)
    {
        foreach (string directory in directories)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            try
            {
                collect(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _diagnostics.Add(Resources.Missing("WASI SDK", exception.Message, directory));
            }
        }
    }

    private void CollectLibraries(string directory, bool includeCppRuntime)
    {
        foreach ((ResourcePurpose purpose, string path) in Resources.LibraryEntries(directory))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!includeCppRuntime && purpose == ResourcePurpose.Library && IsCppRuntime(path))
            {
                continue;
            }

            try
            {
                _resources.Add(purpose, path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _diagnostics.Add(Resources.Missing("WASI SDK", exception.Message, path));
            }
        }
    }

    private void Missing(string group, string file)
    {
        string description = _variant.Identifier is null ? _target.Triple : $"{_target.Triple}/{_variant.Identifier}";
        _diagnostics.Add(Resources.Missing("WASI SDK",
            $"The {description} {group} group is missing '{file}'.", _target.Sysroot));
    }

    private static bool HasDirectoryEntry(string path, ICollection<Diagnostic> diagnostics)
    {
        try
        {
            return Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Resources.Missing("WASI SDK", exception.Message, path));
            return false;
        }
    }

    private static bool HasHeader(IReadOnlyList<Resource> resources, ResourcePurpose purpose, string header) =>
        resources.Any(resource => resource.Purpose == purpose && File.Exists(Path.Combine(resource.Path, header)));

    private static bool IsCppRuntime(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("libc++.", StringComparison.Ordinal)
            || name.StartsWith("libc++abi.", StringComparison.Ordinal)
            || name.StartsWith("libunwind.", StringComparison.Ordinal);
    }

    private sealed record Variant(string? Identifier, string? Directory);

    private sealed record CollectedLayout(TargetLayout Layout, bool HasRequiredResources, bool HasFiles);

    private sealed record TargetDirectories(
        string Sysroot,
        string Triple,
        string IncludeRoot,
        string LibraryRoot,
        string ResourceTriple)
    {
        internal string TargetInclude => Path.Combine(IncludeRoot, ResourceTriple);

        internal string TargetLibrary => Path.Combine(LibraryRoot, ResourceTriple);
    }
}
