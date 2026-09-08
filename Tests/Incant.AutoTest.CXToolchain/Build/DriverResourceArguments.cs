using Incant.CX.FindSdk;

namespace Incant.AutoTest.CXToolchain;

internal static class DriverResourceArguments
{
    internal static IReadOnlyList<string> IncludeDirectories(
        ResolvedToolchain toolchain,
        bool cpp)
    {
        Resource[] resources = toolchain.Resources.ToArray();
        IEnumerable<Resource> selected = cpp
            ? resources
                .Where(resource => resource.Purpose == ResourcePurpose.CppInclude)
                .Concat(resources.Where(
                    resource => resource.Purpose == ResourcePurpose.CInclude))
            : resources.Where(
                resource => resource.Purpose == ResourcePurpose.CInclude);
        return DistinctPaths(selected);
    }

    internal static IReadOnlyList<string> LinkDirectories(
        ResolvedToolchain toolchain)
    {
        IEnumerable<Resource> selected = toolchain.Resources.Where(resource =>
            resource.Purpose is ResourcePurpose.LibraryDirectory
                or ResourcePurpose.RuntimeDirectory);
        if (toolchain.AdapterKind == BuildAdapterKind.Android)
        {
            selected = selected.OrderBy(resource => resource.ApiLevel is null ? 1 : 0);
        }

        return DistinctPaths(selected);
    }

    internal static IReadOnlyList<string> FrameworkDirectories(
        ResolvedToolchain toolchain) =>
        DistinctPaths(toolchain.Resources.Where(
            resource => resource.Purpose == ResourcePurpose.Framework));

    private static IReadOnlyList<string> DistinctPaths(
        IEnumerable<Resource> resources)
    {
        var identities = new HashSet<string>(PathIdentity.Comparer);
        var paths = new List<string>();
        foreach (Resource resource in resources)
        {
            if (identities.Add(PathIdentity.Normalize(resource.Path)))
            {
                paths.Add(resource.Path);
            }
        }

        return paths;
    }
}
