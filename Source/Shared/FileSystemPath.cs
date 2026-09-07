namespace Incant.Internal;

// Shared source keeps discovery and AutoTest path identities consistent without a public API.
internal static class FileSystemPath
{
    internal static string Absolute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OperatingSystem.IsWindows())
        {
            return Path.GetFullPath(path);
        }

        if (path.Contains('\0'))
        {
            throw new ArgumentException("A path cannot contain a null character.", nameof(path));
        }

        return Path.IsPathRooted(path) ? path : Path.Combine(Environment.CurrentDirectory, path);
    }

    internal static string Resolve(string path)
    {
        string absolute = Absolute(path);
        int links = 0;
        return Path.TrimEndingDirectorySeparator(ResolveComponents(absolute, ref links));
    }

    private static string ResolveComponents(string path, ref int links)
    {
        string root = Path.GetPathRoot(path)!;
        string current = root;
        foreach (string component in path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                if (!Directory.Exists(current))
                {
                    throw new IOException($"Cannot traverse the parent of the missing directory '{current}'.");
                }

                current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)) ?? root;
                continue;
            }

            string next = Path.Combine(current, component);
            FileSystemInfo information = Directory.Exists(next)
                ? new DirectoryInfo(next) : new FileInfo(next);
            string? target = information.LinkTarget;
            if (target is null)
            {
                current = next;
                continue;
            }

            if (++links > 40)
            {
                throw new IOException($"Too many symbolic links while resolving '{path}'.");
            }

            current = ResolveComponents(
                Path.IsPathRooted(target) ? target : Path.Combine(current, target), ref links);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                throw new IOException($"The symbolic link '{next}' has a missing target '{current}'.");
            }
        }

        return current;
    }
}
