using System.Collections.Concurrent;

namespace Incant.AutoTest.CXLegacyToolchain;

internal static class PathIdentity
{
    private static readonly ConcurrentDictionary<string, string> s_normalizedPaths =
        new(Comparer);

    internal static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Incant.Internal.FileSystemPath.Absolute(path);
        return s_normalizedPaths.GetOrAdd(fullPath, ResolveLinks);
    }

    internal static bool AreEqual(string left, string right) =>
        Comparer.Equals(Normalize(left), Normalize(right));

    internal static bool Contains(string root, string path)
    {
        string normalizedRoot = Normalize(root);
        string normalizedPath = Normalize(path);
        string relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        return relative == "."
            || !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            && !relative.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    internal static bool Related(string left, string right) =>
        Contains(left, right) || Contains(right, left);

    private static string ResolveLinks(string path) =>
        Incant.Internal.FileSystemPath.Resolve(path);
}
