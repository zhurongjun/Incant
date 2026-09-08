using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Incant.Base;

public sealed partial class FileList
{
    private Selector CreateSelector(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (path.Contains('\0'))
        {
            throw new ArgumentException("Paths cannot contain a null character.", parameterName);
        }

        string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        int wildcard = normalized.IndexOf('*');
        bool directoryPattern = Path.EndsInDirectorySeparator(normalized);
        if (wildcard < 0 && !directoryPattern)
        {
            return new Selector(GetAbsolutePath(normalized, parameterName), null, null);
        }

        // Anchor at the literal prefix, so matching never scans unrelated ancestors or volumes.
        int separator = wildcard < 0 ? normalized.Length - 1
            : normalized.LastIndexOf(Path.DirectorySeparatorChar, wildcard);
        string prefix = separator < 0 ? Path.GetPathRoot(normalized)! : normalized[..(separator + 1)];
        int patternStart = separator < 0 ? prefix.Length : separator + 1;
        string pattern = normalized[patternStart..].Replace(Path.DirectorySeparatorChar, '/');
        if (prefix.Length == 0)
        {
            prefix = ".";
        }
        if (pattern.Length == 0)
        {
            pattern = "**/*";
        }
        string root = Path.TrimEndingDirectorySeparator(GetAbsolutePath(prefix, parameterName));
        var matcher = new Matcher(OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        try
        {
            matcher.AddInclude(pattern);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The glob pattern is invalid.", parameterName, exception);
        }
        return new Selector(root, pattern, matcher);
    }

    private string GetAbsolutePath(string path, string parameterName)
    {
        try
        {
            return Path.GetFullPath(path, RootDirectory);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The path is invalid.", parameterName, exception);
        }
    }

    private sealed record Selector(string Path, string? Pattern, Matcher? Matcher);

    private sealed class MemoryIndex
    {
        private readonly Dictionary<string, MemoryDirectory> _directories = new(s_pathComparer);
        private readonly Dictionary<string, string[]> _matches = new(s_pathComparer);

        internal void Add(string path)
        {
            string? parentPath = Path.GetDirectoryName(path);
            if (parentPath is not null)
            {
                MemoryDirectory directory = EnsureDirectory(parentPath);
                directory.Files.Add(Path.GetFileName(path), new IndexedFile(path, directory));
                _matches.Clear();
            }
        }

        internal void Remove(string path)
        {
            string? parentPath = Path.GetDirectoryName(path);
            if (parentPath is null || !_directories.TryGetValue(parentPath, out MemoryDirectory? directory))
            {
                return;
            }
            directory.Files.Remove(Path.GetFileName(path));
            _matches.Clear();
            while (directory.Files.Count == 0 && directory.Directories.Count == 0)
            {
                _directories.Remove(directory.FullName);
                if (directory.ParentDirectory is not MemoryDirectory parent)
                {
                    break;
                }
                parent.Directories.Remove(directory.Name);
                directory = parent;
            }
        }

        internal string[] Match(Selector selector)
        {
            string key = Path.Combine(selector.Path, selector.Pattern!);
            if (_matches.TryGetValue(key, out string[]? paths))
            {
                return paths;
            }
            if (!_directories.TryGetValue(selector.Path, out MemoryDirectory? root))
            {
                paths = [];
            }
            else
            {
                PatternMatchingResult result = selector.Matcher!.Execute(root);
                paths = result.Files.Select(file => Path.GetFullPath(file.Path, selector.Path)).ToArray();
            }
            // Metadata changes leave membership intact; Add and Remove invalidate these matches.
            _matches.Add(key, paths);
            return paths;
        }

        private MemoryDirectory EnsureDirectory(string path)
        {
            if (_directories.TryGetValue(path, out MemoryDirectory? directory))
            {
                return directory;
            }
            string? parentPath = Path.GetDirectoryName(path);
            MemoryDirectory? parent = parentPath is null ? null : EnsureDirectory(parentPath);
            directory = new MemoryDirectory(path, parent);
            _directories.Add(path, directory);
            parent?.Directories.Add(directory.Name, directory);
            return directory;
        }
    }

    private sealed class MemoryDirectory(string path, MemoryDirectory? parent) : DirectoryInfoBase
    {
        internal Dictionary<string, MemoryDirectory> Directories { get; } = new(s_pathComparer);

        internal Dictionary<string, IndexedFile> Files { get; } = new(s_pathComparer);

        public override string FullName { get; } = path;

        public override string Name { get; } = Path.GetFileName(path);

        public override DirectoryInfoBase? ParentDirectory { get; } = parent;

        public override IEnumerable<FileSystemInfoBase> EnumerateFileSystemInfos()
        {
            foreach (IndexedFile file in Files.Values)
            {
                yield return file;
            }
            foreach (MemoryDirectory directory in Directories.Values)
            {
                yield return directory;
            }
        }

        public override DirectoryInfoBase GetDirectory(string name) => Directories.TryGetValue(name, out MemoryDirectory? directory)
            ? directory : new MemoryDirectory(Path.GetFullPath(name, FullName), this);

        public override FileInfoBase? GetFile(string name) => Files.GetValueOrDefault(name);
    }

    private sealed class IndexedFile(string path, DirectoryInfoBase parent) : FileInfoBase
    {
        public override string FullName { get; } = path;

        public override string Name { get; } = Path.GetFileName(path);

        public override DirectoryInfoBase ParentDirectory { get; } = parent;
    }

    private sealed class DiskTraversal
    {
        private readonly Dictionary<string, string[]> _matches = new(s_pathComparer);
        private readonly Dictionary<string, string?> _physicalPaths = new(s_pathComparer);
        private readonly Dictionary<string, DiskChild[]> _directories = new(s_pathComparer);

        internal string[] Match(Selector selector)
        {
            string key = Path.Combine(selector.Path, selector.Pattern!);
            if (_matches.TryGetValue(key, out string[]? paths))
            {
                return paths;
            }
            var root = new DiskDirectory(this, selector.Path, null);
            PatternMatchingResult result = selector.Matcher!.Execute(root);
            paths = result.Files.Select(file => Path.GetFullPath(file.Path, selector.Path)).ToArray();
            Array.Sort(paths, ComparePaths);
            _matches.Add(key, paths);
            return paths;
        }

        internal string? GetPhysicalPath(string path) => _physicalPaths.TryGetValue(path, out string? cached)
            ? cached : GetPhysicalPath(path, new HashSet<string>(s_pathComparer));

        internal DiskChild[] ReadDirectory(string physicalPath)
        {
            if (_directories.TryGetValue(physicalPath, out DiskChild[]? cached))
            {
                return cached;
            }

            var children = new List<DiskChild>();
            try
            {
                // Do not use Exists: it converts access errors into a misleading empty glob.
                foreach (FileSystemInfo entry in new DirectoryInfo(physicalPath).EnumerateFileSystemInfos())
                {
                    children.Add(new DiskChild(entry.Name, (entry.Attributes & FileAttributes.Directory) != 0));
                }
            }
            catch (DirectoryNotFoundException)
            {
                children.Clear();
            }
            DiskChild[] result = children.ToArray();
            _directories.Add(physicalPath, result);
            return result;
        }

        private string? GetPhysicalPath(string path, HashSet<string> pending)
        {
            if (_physicalPaths.TryGetValue(path, out string? cached))
            {
                return cached;
            }
            if (!pending.Add(path))
            {
                return null;
            }
            try
            {
                string? parentPath = Path.GetDirectoryName(path);
                string? parent = parentPath is null ? null : GetPhysicalPath(parentPath, pending);
                if (parentPath is not null && parent is null)
                {
                    _physicalPaths.Add(path, null);
                    return null;
                }
                string candidate = parent is null ? path : Path.Combine(parent, Path.GetFileName(path));
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(candidate);
                }
                catch (FileNotFoundException)
                {
                    _physicalPaths.Add(path, null);
                    return null;
                }
                catch (DirectoryNotFoundException)
                {
                    _physicalPaths.Add(path, null);
                    return null;
                }

                string? result;
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    && new DirectoryInfo(candidate).LinkTarget is string target)
                {
                    result = ResolveLinkTarget(target, Path.GetDirectoryName(candidate)!, pending);
                }
                else if ((attributes & FileAttributes.Directory) == 0)
                {
                    throw new IOException($"The glob directory '{path}' is not a directory.");
                }
                else
                {
                    result = candidate;
                }
                _physicalPaths.Add(path, result);
                return result;
            }
            finally
            {
                pending.Remove(path);
            }
        }

        private string? ResolveLinkTarget(string target, string parentPath, HashSet<string> pending)
        {
            if (OperatingSystem.IsWindows())
            {
                string absolute = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target, parentPath));
                return GetPhysicalPath(absolute, pending);
            }

            // Unix resolves each link before a subsequent '..' in its target, unlike lexical normalization.
            string? current = Path.IsPathRooted(target) ? Path.GetPathRoot(target)! : parentPath;
            foreach (string component in target.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (component == ".")
                {
                    continue;
                }
                if (component == "..")
                {
                    current = Path.GetDirectoryName(current) ?? current;
                    continue;
                }
                current = GetPhysicalPath(Path.Combine(current, component), pending);
                if (current is null)
                {
                    return null;
                }
            }
            return current;
        }

        private static int ComparePaths(string left, string right)
        {
            int result = s_pathComparer.Compare(left, right);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left, right);
        }
    }

    private readonly record struct DiskChild(string Name, bool IsDirectory);

    private sealed class DiskDirectory(DiskTraversal traversal, string path, DiskDirectory? parent) : DirectoryInfoBase
    {
        private string? _physicalPath;
        private bool _identityResolved;

        public override string FullName { get; } = path;

        public override string Name { get; } = Path.GetFileName(path);

        public override DirectoryInfoBase? ParentDirectory => parent;

        private string? PhysicalPath
        {
            get
            {
                if (!_identityResolved)
                {
                    _physicalPath = traversal.GetPhysicalPath(FullName);
                    _identityResolved = true;
                }
                return _physicalPath;
            }
        }

        public override IEnumerable<FileSystemInfoBase> EnumerateFileSystemInfos()
        {
            string? physicalPath = PhysicalPath;
            if (physicalPath is null)
            {
                yield break;
            }
            for (DiskDirectory? ancestor = parent; ancestor is not null; ancestor = ancestor.ParentDirectory as DiskDirectory)
            {
                if (s_pathComparer.Equals(physicalPath, ancestor.PhysicalPath))
                {
                    yield break;
                }
            }

            foreach (DiskChild child in traversal.ReadDirectory(physicalPath))
            {
                string childPath = Path.Combine(FullName, child.Name);
                yield return child.IsDirectory
                    ? new DiskDirectory(traversal, childPath, this) : new IndexedFile(childPath, this);
            }
        }

        public override DirectoryInfoBase GetDirectory(string name) => new DiskDirectory(traversal, Path.GetFullPath(name, FullName), this);

        public override FileInfoBase GetFile(string name) => new IndexedFile(Path.GetFullPath(name, FullName), this);
    }
}
