namespace Incant.Core.Cpp.FindSdk;

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
        IReadOnlyList<string> triples = WasiTargetResolver.ResourceTriples(targetTriple);
        string[] includeRoots = [Path.Combine(sysroot, "include"), Path.Combine(sysroot, "usr", "include")];
        string[] libraryRoots = [Path.Combine(sysroot, "lib"), Path.Combine(sysroot, "usr", "lib")];
        var target = new TargetDirectories(sysroot, targetTriple, includeRoots,
            triples.SelectMany(triple => includeRoots.Select(root => Path.Combine(root, triple))).ToArray(),
            triples.SelectMany(triple => libraryRoots.Select(root => Path.Combine(root, triple))).ToArray());
        var diagnostics = new List<Diagnostic>();
        Variant[] variants = s_variants.Where(variant =>
            target.TargetIncludes.Concat(target.TargetLibraries)
                .Any(path => HasDirectoryEntry(Path.Combine(path, variant.Directory!), diagnostics))).ToArray();
        if (variants.Length == 0)
        {
            variants = [new Variant(null, null)];
        }

        return variants.Select(variant => new WasiResourceLayout(target, variant, diagnostics, cancellationToken)
            .Collect()).ToArray();
    }

    private TargetLayout Collect()
    {
        IEnumerable<string> cppDirectories = _variant.Directory is null
            ? _target.TargetIncludes.Concat(_target.IncludeRoots).Select(path => Path.Combine(path, "c++", "v1"))
            : _target.TargetIncludes.Select(path => Path.Combine(path, _variant.Directory, "c++", "v1"));
        CollectDirectories(cppDirectories, path => _resources.Add(ResourcePurpose.CppInclude, path));
        // C headers also serve C++, but cannot establish a C++ standard-library header group.
        bool hasCppHeaders = HasHeader(_resources.Build(), ResourcePurpose.CppInclude, "array");
        CollectDirectories(_target.TargetIncludes.Concat(_target.IncludeRoots),
            path => Resources.Headers(_resources, path));

        if (_variant.Directory is not null)
        {
            CollectDirectories(_target.TargetLibraries.Select(path => Path.Combine(path, _variant.Directory)),
                path => CollectLibraries(path, includeCppRuntime: true));
        }

        CollectDirectories(_target.TargetLibraries,
            path => CollectLibraries(path, includeCppRuntime: _variant.Directory is null));
        IReadOnlyList<Resource> resources = _resources.Build();
        if (!HasHeader(resources, ResourcePurpose.CInclude, "stdio.h"))
        {
            Missing("C header", "stdio.h");
        }

        if (!hasCppHeaders)
        {
            Missing("C++ header", "array");
        }

        string[] requiredLibraries = _variant.Identifier == "eh"
            ? ["libc.a", "libc++.a", "libc++abi.a", "libunwind.a"]
            : ["libc.a", "libc++.a", "libc++abi.a"];
        foreach (string library in requiredLibraries)
        {
            if (!resources.Any(resource => resource.Purpose == ResourcePurpose.Library
                && Path.GetFileName(resource.Path) == library))
            {
                Missing("library", library);
            }
        }

        return new TargetLayout(TargetPlatform.Wasi, TargetArchitecture.Wasm32, resources,
            _target.Triple, _target.Sysroot, multilib: _variant.Identifier, diagnostics: _diagnostics.Distinct());
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

    private sealed record TargetDirectories(
        string Sysroot,
        string Triple,
        IReadOnlyList<string> IncludeRoots,
        IReadOnlyList<string> TargetIncludes,
        IReadOnlyList<string> TargetLibraries);
}
