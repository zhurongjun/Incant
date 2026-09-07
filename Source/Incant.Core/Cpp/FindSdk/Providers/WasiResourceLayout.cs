namespace Incant.Core.Cpp.FindSdk;

internal static class WasiResourceLayout
{
    internal static TargetLayout Create(string sysroot, string targetTriple)
    {
        string resourceTriple = WasiTargetResolver.ResolveResourceTriple(sysroot, targetTriple);
        string[] roots = [Path.Combine(sysroot, "include"), Path.Combine(sysroot, "usr", "include")];
        var collector = new ResourceCollector(sysroot);
        var diagnostics = new List<Diagnostic>();

        void CollectDirectory(string path, Action collect)
        {
            try
            {
                collect();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Resources.Missing("WASI SDK", exception.Message, path));
            }
        }

        foreach (string root in roots)
        {
            string directory = Path.Combine(root, resourceTriple, "c++", "v1");
            CollectDirectory(directory, () => collector.Add(ResourcePurpose.CppInclude, directory));
        }

        foreach (string root in roots)
        {
            string directory = Path.Combine(root, "c++", "v1");
            CollectDirectory(directory, () => collector.Add(ResourcePurpose.CppInclude, directory));
        }

        foreach (string root in roots)
        {
            string directory = Path.Combine(root, resourceTriple);
            CollectDirectory(directory, () => Resources.Headers(collector, directory));
        }

        foreach (string root in roots)
        {
            CollectDirectory(root, () => Resources.Headers(collector, root));
        }

        foreach (string root in new[] { Path.Combine(sysroot, "lib"), Path.Combine(sysroot, "usr", "lib") })
        {
            string directory = Path.Combine(root, resourceTriple);
            CollectDirectory(directory, () => Resources.Libraries(collector, directory));
        }

        IReadOnlyList<Resource> resources = collector.Build();
        foreach ((ResourcePurpose purpose, string header) in new[]
        {
            (ResourcePurpose.CInclude, "stdio.h"),
            (ResourcePurpose.CppInclude, "array"),
        })
        {
            if (!resources.Any(resource => resource.Purpose == purpose
                && File.Exists(Path.Combine(resource.Path, header))))
            {
                diagnostics.Add(Resources.Missing("WASI SDK",
                    $"The {targetTriple} {purpose} layout is missing '{header}'.", sysroot));
            }
        }

        if (!resources.Any(resource => resource.Purpose == ResourcePurpose.Library))
        {
            diagnostics.Add(Resources.Missing("WASI SDK",
                $"No libraries were found for {targetTriple}.", sysroot));
        }

        return new TargetLayout(TargetPlatform.Wasi, TargetArchitecture.Wasm32, resources,
            targetTriple, sysroot, diagnostics: diagnostics);
    }
}
