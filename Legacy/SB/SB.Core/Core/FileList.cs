using SB.Core;
using Microsoft.Extensions.FileSystemGlobbing;

namespace SB
{
    public class FileOptions
    {
        public ArgumentDictionary Arguments { get; } = new();
    }

    public abstract class FileList
    {
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        public FileList AddFiles(params string[] files)
        {
            AddFiles(null, files);
            return this;
        }

        public FileList AddFiles(FileOptions? Options, params string[] files)
        {
            lock (FileOperationsLock)
            {
                var Directory = Target!.Directory!;
                foreach (var File in files)
                {
                    string FileToAdd;
                    if (File.Contains("*"))
                    {
                        FileToAdd = Path.IsPathFullyQualified(File) ? Path.GetRelativePath(Directory, File) : File;
                        Globs.Add(FileToAdd);
                    }
                    else
                    {
                        FileToAdd = Path.GetFullPath(Path.IsPathFullyQualified(File) ? File : Path.Combine(Directory, File));
                        Absolutes.Add(FileToAdd);
                    }
                    if (Options is not null)
                        FileOptions.TryAdd(FileToAdd, Options);
                }
            }
            return this;
        }

        public FileList RemoveFiles(params string[] files)
        {
            var Directory = Target!.Directory!;
            lock (FileOperationsLock)
            {
                foreach (var file in files)
                {
                    if (file.Contains("*"))
                    {
                        if (Path.IsPathFullyQualified(file))
                            Globs.Remove(Path.GetRelativePath(Directory, file));
                        else
                            Globs.Remove(file);
                    }
                    else if (Path.IsPathFullyQualified(file))
                        Absolutes.Remove(Path.GetFullPath(file));
                    else
                        Absolutes.Remove(Path.GetFullPath(Path.Combine(Directory, file)));
                }
            }
            return this;
        }

        public FileOptions? GetFileOptions(string file)
        {
            if (FileOptions.TryGetValue(file, out var Options))
                return Options;
            return null;
        }

        public void GlobFiles()
        {
            if (Globs.Count != 0)
            {
                foreach (var Glob in Globs)
                {
                    var GlobMatcher = new Matcher();
                    GlobMatcher.AddInclude(Glob);
                    
                    var GlobResults = GlobMatcher.GetResultsInFullPath(Target!.Directory!)
                        .Select(Path.GetFullPath)
                        .ToArray();
                    Absolutes.AddRange(GlobResults);

                    FileOptions.TryGetValue(Glob, out var GlobOptions);
                    if (GlobOptions is not null)
                        GlobResults.All(F => FileOptions.TryAdd(F, GlobOptions));
                }
            }
        }

        public bool Is<T>() => this is T;

        public Target? Target { get; init; }
        public IReadOnlySet<string> Files => Absolutes;
        public Dictionary<string, FileOptions> FileOptions { get; } = new(PathComparer);
        private SortedSet<string> Globs = new();
        private SortedSet<string> Absolutes = new(PathComparer);
        private System.Threading.Lock FileOperationsLock = new();
    }
}
