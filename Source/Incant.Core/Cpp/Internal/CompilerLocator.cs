using System.Text.RegularExpressions;

namespace Incant.Core.Cpp;

internal static partial class CompilerLocator
{
    internal static async Task<IReadOnlyList<Candidate>> FindAsync(
        bool isGnu, string? explicitRoot, DiscoveryContext context, CancellationToken cancellationToken)
    {
        if (isGnu && OperatingSystem.IsWindows())
        {
            return [];
        }

        var roots = new List<Candidate>();
        if (explicitRoot is not null)
        {
            roots.Add(new Candidate(explicitRoot, Source.Explicit));
        }
        else
        {
            foreach (string name in isGnu ? new[] { "CC", "CXX" } : new[] { "LLVM_PATH", "CC", "CXX" })
            {
                string? path = context.GetEnvironmentVariable(name);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!Path.IsPathFullyQualified(path))
                {
                    path = SearchPaths.OnPath(context, path);
                }

                if (path is not null)
                {
                    roots.Add(new Candidate(path, Source.Environment));
                }
            }

            foreach (string directory in new[] { "/usr/bin", "/usr/local/bin", "/opt/homebrew/bin",
                "/opt/homebrew/opt/llvm/bin", "/usr/local/opt/llvm/bin" })
            {
                if (Directory.Exists(directory))
                {
                    roots.Add(new Candidate(directory, Source.StandardPath));
                }
            }

            if (!isGnu && OperatingSystem.IsWindows())
            {
                if (context.GetEnvironmentVariable("ProgramFiles") is string programFiles)
                {
                    roots.Add(new Candidate(Path.Combine(programFiles, "LLVM"), Source.StandardPath));
                }

                foreach (Candidate visualStudio in await WindowsLocator.VisualStudiosAsync(null, context, cancellationToken).ConfigureAwait(false))
                {
                    foreach (string suffix in new[] { "bin", "x64/bin", "ARM64/bin" })
                    {
                        roots.Add(new Candidate(Path.Combine(visualStudio.Path, "VC", "Tools", "Llvm", suffix), visualStudio.Sources.Min()));
                    }
                }
            }

            roots.AddRange(SearchPaths.PathDirectories(context).Select(path => new Candidate(path, Source.Path)));
        }

        var compilers = new List<Candidate>();
        foreach (Candidate root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> paths = File.Exists(root.Path) ? [root.Path]
                : new[] { root.Path, Path.Combine(root.Path, "bin") }.SelectMany(SearchPaths.Files);
            foreach (string path in paths)
            {
                string name = Path.GetFileName(path);
                if (!(isGnu ? GnuName() : ClangName()).IsMatch(name))
                {
                    continue;
                }

                string primary = path.Replace("g++", "gcc", StringComparison.Ordinal)
                    .Replace("clang++", "clang", StringComparison.Ordinal);
                string clangDriver = primary.Replace("clang-cl", "clang", StringComparison.Ordinal);
                if (!isGnu && File.Exists(clangDriver))
                {
                    primary = clangDriver;
                }

                if (File.Exists(primary))
                {
                    compilers.Add(new Candidate(primary, root.Sources.Min()));
                }
            }
        }

        return Candidate.Merge(compilers);
    }

    internal static string ExecutableStem(string path) => OperatingSystem.IsWindows()
        ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);

    internal static string RelatedName(string compiler, string name, bool isGnu)
    {
        string stem = ExecutableStem(compiler);
        string marker = isGnu ? "gcc" : stem.Contains("clang-cl", StringComparison.Ordinal) ? "clang-cl" : "clang";
        int index = stem.LastIndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? name : stem[..index] + name + stem[(index + marker.Length)..];
    }

    [GeneratedRegex(@"^(?:[A-Za-z0-9_+.-]+-)?(?:gcc|g\+\+)(?:-\d+(?:\.\d+)*)?$")]
    private static partial Regex GnuName();

    [GeneratedRegex(@"^(?:clang|clang\+\+|clang-cl)(?:-\d+(?:\.\d+)*)?(?:\.exe)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClangName();
}
