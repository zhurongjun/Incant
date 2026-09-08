using Incant.CXLegacy;

namespace Incant.CXLegacy.FindSdk;

/// <summary>Describes conventional resource groups; the collector owns path handling and snapshot construction.</summary>
internal static class Resources
{
    internal static void Headers(ResourceCollector resources, string path)
    {
        resources.Add(ResourcePurpose.CInclude, path);
        resources.Add(ResourcePurpose.CppInclude, path);
    }

    internal static void Libraries(ResourceCollector resources, string directory, int? apiLevel = null)
    {
        foreach ((ResourcePurpose purpose, string path) in LibraryEntries(directory))
        {
            resources.Add(purpose, path, apiLevel);
        }
    }

    internal static IEnumerable<(ResourcePurpose Purpose, string Path)> LibraryEntries(string directory)
    {
        yield return (ResourcePurpose.LibraryDirectory, directory);
        foreach (string file in SearchPaths.Files(directory))
        {
            if (TargetResources.IsStartup(file))
            {
                yield return (ResourcePurpose.Startup, file);
            }
            else if (TargetResources.IsLibrary(file))
            {
                yield return (ResourcePurpose.Library, file);
            }
        }
    }

    internal static IReadOnlyList<Resource> Sysroot(string root, string? triple = null)
    {
        var resources = new ResourceCollector(root);
        resources.Add(ResourcePurpose.CppInclude, Path.Combine(root, "usr", "include", "c++", "v1"));
        resources.Add(ResourcePurpose.CppInclude, Path.Combine(root, "include", "c++", "v1"));
        if (triple is not null)
        {
            Headers(resources, Path.Combine(root, "usr", "include", triple));
        }

        Headers(resources, Path.Combine(root, "usr", "include"));
        Headers(resources, Path.Combine(root, "include"));
        foreach (string suffix in new[] { "usr/lib", "usr/lib64", "lib", "lib64" })
        {
            string directory = Path.Combine(root, suffix);
            if (triple is not null && Directory.Exists(Path.Combine(directory, triple)))
            {
                Libraries(resources, Path.Combine(directory, triple));
            }

            Libraries(resources, directory);
        }

        resources.Add(ResourcePurpose.Framework, Path.Combine(root, "System", "Library", "Frameworks"));
        return resources.Build();
    }

    internal static Diagnostic Missing(string provider, string message, string path) =>
        new(DiagnosticSeverity.Warning, "missing-resource", provider, message, path);
}
