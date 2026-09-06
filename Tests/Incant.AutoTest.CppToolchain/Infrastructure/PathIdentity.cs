using System.Collections.Concurrent;

namespace Incant.AutoTest.CppToolchain;

internal static class PathIdentity
{
    private static readonly ConcurrentDictionary<string, string> s_normalizedPaths =
        new(Comparer);

    internal static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
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

    private static string ResolveLinks(string fullPath)
    {
        string root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException(
                $"Path '{fullPath}' has no filesystem root.",
                nameof(fullPath));
        string current = root;
        foreach (string component in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileSystemInfo? information = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;
            if (information is not null)
            {
                current = information.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? current;
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }
}
