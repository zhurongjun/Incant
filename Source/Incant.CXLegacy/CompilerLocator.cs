using System.Text.RegularExpressions;

namespace Incant.CXLegacy;

internal static partial class CompilerLocator
{
    internal static async Task<IReadOnlyList<CompilerInvocationCandidate>> FindAsync(
        string? explicitRoot,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CompilerInvocationCandidate> found =
            await Task.Run(
                () => Find(
                    explicitRoot,
                    context,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        if (explicitRoot is not null || !OperatingSystem.IsWindows())
        {
            return found;
        }

        var candidates =
            new List<CompilerInvocationCandidate>(found);
        if (context.GetEnvironmentVariable("ProgramFiles")
            is string programFiles)
        {
            AddRoot(
                candidates,
                Path.Combine(programFiles, "LLVM"),
                Source.StandardPath,
                isPrivate: true,
                cancellationToken);
        }

        foreach (Candidate visualStudio
            in await WindowsLocator.VisualStudiosAsync(
                null,
                context,
                cancellationToken).ConfigureAwait(false))
        {
            foreach (string suffix in new[]
            {
                "bin",
                Path.Combine("x64", "bin"),
                Path.Combine("ARM64", "bin"),
            })
            {
                AddRoot(
                    candidates,
                    Path.Combine(
                        visualStudio.Path,
                        "VC",
                        "Tools",
                        "Llvm",
                        suffix),
                    visualStudio.Sources.Min(),
                    isPrivate: true,
                    cancellationToken);
            }
        }

        return CompilerInvocationCandidate.Merge(candidates);
    }

    internal static bool IsSharedDirectory(string path)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string unixPath = normalized.Replace('\\', '/');
        return unixPath is "/bin" or "/usr/bin" or "/usr/local/bin"
            || unixPath.EndsWith("/usr/bin", StringComparison.Ordinal)
            || IsHomebrewCommonBin(unixPath);
    }

    private static IReadOnlyList<CompilerInvocationCandidate> Find(
        string? explicitRoot,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        var candidates = new List<CompilerInvocationCandidate>();
        IReadOnlyList<CompilerSearchDirectory> nixBinTools =
            EnvironmentSearchDirectories(context, "NIX_BINTOOLS");
        if (explicitRoot is not null)
        {
            AddRoot(
                candidates,
                explicitRoot,
                Source.Explicit,
                isPrivate: null,
                cancellationToken,
                NixAssociations(explicitRoot, context, nixBinTools));
            return CompilerInvocationCandidate.Merge(candidates);
        }

        AddEnvironmentCommand(
            candidates,
            context,
            "CC",
            nixBinTools,
            cancellationToken);
        AddEnvironmentCommand(
            candidates,
            context,
            "CXX",
            nixBinTools,
            cancellationToken);
        AddEnvironmentRoot(
            candidates,
            context,
            "LLVM_PATH",
            cancellationToken);
        AddEnvironmentPrefix(
            candidates,
            context,
            "CONDA_PREFIX",
            cancellationToken);
        AddEnvironmentPrefix(
            candidates,
            context,
            "NIX_CC",
            cancellationToken,
            nixBinTools);

        if (!OperatingSystem.IsWindows())
        {
            AddUnixRoots(
                candidates,
                context,
                cancellationToken);
        }

        foreach (string directory in SearchPaths.PathDirectories(context))
        {
            AddRoot(
                candidates,
                directory,
                Source.Path,
                isPrivate: false,
                cancellationToken,
                NixAssociations(directory, context, nixBinTools));
        }

        return CompilerInvocationCandidate.Merge(candidates);
    }

    private static void AddUnixRoots(
        ICollection<CompilerInvocationCandidate> candidates,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        var homebrewPrefixes =
            new Dictionary<string, Source>(SearchPaths.Comparer);
        AddHomebrewPrefix(
            homebrewPrefixes,
            context.GetEnvironmentVariable("HOMEBREW_PREFIX"),
            Source.Environment);
        AddHomebrewPrefix(
            homebrewPrefixes,
            "/home/linuxbrew/.linuxbrew",
            Source.StandardPath);
        AddHomebrewPrefix(
            homebrewPrefixes,
            "/opt/homebrew",
            Source.StandardPath);
        AddHomebrewPrefix(
            homebrewPrefixes,
            "/usr/local",
            Source.StandardPath);
        foreach ((string prefix, Source source) in homebrewPrefixes
            .OrderBy(entry => entry.Value)
            .ThenBy(entry => entry.Key, SearchPaths.Comparer))
        {
            AddHomebrewRoots(
                candidates,
                prefix,
                source,
                cancellationToken);
        }

        foreach (string directory in new[] { "/usr/bin", "/usr/local/bin" })
        {
            AddRoot(
                candidates,
                directory,
                Source.StandardPath,
                isPrivate: false,
                cancellationToken);
        }

        AddLlvmSlotRoots(candidates, "/usr/lib", cancellationToken);
        AddLlvmSlotRoots(candidates, "/usr/lib64", cancellationToken);
    }

    private static void AddEnvironmentCommand(
        ICollection<CompilerInvocationCandidate> candidates,
        DiscoveryContext context,
        string name,
        IReadOnlyList<CompilerSearchDirectory> nixBinTools,
        CancellationToken cancellationToken)
    {
        string? value = context.GetEnvironmentVariable(name)?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string? path = Path.IsPathFullyQualified(value)
            ? value
            : SearchPaths.PathDirectories(context)
                .Select(directory => SearchPaths.Executable(
                    directory,
                    value,
                    wrappers: true))
                .FirstOrDefault(candidate => candidate is not null);
        if (path is not null)
        {
            AddRoot(
                candidates,
                path,
                Source.Environment,
                isPrivate: null,
                cancellationToken,
                NixAssociations(path, context, nixBinTools));
        }
    }

    private static void AddEnvironmentRoot(
        ICollection<CompilerInvocationCandidate> candidates,
        DiscoveryContext context,
        string name,
        CancellationToken cancellationToken)
    {
        string? value = context.GetEnvironmentVariable(name)?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(value))
        {
            AddRoot(
                candidates,
                value,
                Source.Environment,
                isPrivate: true,
                cancellationToken);
        }
    }

    private static void AddEnvironmentPrefix(
        ICollection<CompilerInvocationCandidate> candidates,
        DiscoveryContext context,
        string name,
        CancellationToken cancellationToken,
        IReadOnlyList<CompilerSearchDirectory>? associatedDirectories = null)
    {
        string? value = context.GetEnvironmentVariable(name)?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string root = string.Equals(
            Path.GetFileName(value),
            "bin",
            StringComparison.OrdinalIgnoreCase)
            ? value
            : Path.Combine(value, "bin");
        AddRoot(
            candidates,
            root,
            Source.Environment,
            isPrivate: true,
            cancellationToken,
            associatedDirectories);
    }

    private static IReadOnlyList<CompilerSearchDirectory>
        EnvironmentSearchDirectories(
            DiscoveryContext context,
            string name)
    {
        string? value = context.GetEnvironmentVariable(name)?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            string directory = string.Equals(
                Path.GetFileName(value),
                "bin",
                StringComparison.OrdinalIgnoreCase)
                ? value
                : Path.Combine(value, "bin");
            return Directory.Exists(directory)
                ? [new CompilerSearchDirectory(
                    Path.GetFullPath(directory),
                    IsPrivate: true)]
                : [];
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return [];
        }
    }

    private static IReadOnlyList<CompilerSearchDirectory> NixAssociations(
        string root,
        DiscoveryContext context,
        IReadOnlyList<CompilerSearchDirectory> directories)
    {
        string? compilerRoot =
            context.GetEnvironmentVariable("NIX_CC");
        if (directories.Count == 0
            || string.IsNullOrWhiteSpace(compilerRoot))
        {
            return [];
        }

        try
        {
            string normalizedRoot = SearchPaths.Normalize(root);
            string normalizedCompiler =
                SearchPaths.Normalize(compilerRoot);
            return SearchPaths.Related(
                normalizedRoot,
                normalizedCompiler)
                ? directories
                : [];
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void AddHomebrewPrefix(
        IDictionary<string, Source> prefixes,
        string? value,
        Source source)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string candidate = value.Trim().Trim('"');
        if (!Directory.Exists(candidate))
        {
            return;
        }

        string path = Path.GetFullPath(candidate);
        if (!prefixes.TryGetValue(path, out Source existing)
            || source < existing)
        {
            prefixes[path] = source;
        }
    }

    private static void AddHomebrewRoots(
        ICollection<CompilerInvocationCandidate> candidates,
        string prefix,
        Source source,
        CancellationToken cancellationToken)
    {
        AddRoot(
            candidates,
            Path.Combine(prefix, "bin"),
            source,
            isPrivate: false,
            cancellationToken);
        string opt = Path.Combine(prefix, "opt");
        foreach (string directory in ReadDirectories(opt)
            .Where(path => IsCompilerFormula(Path.GetFileName(path)))
            .OrderBy(path => path, SearchPaths.Comparer))
        {
            AddRoot(
                candidates,
                directory,
                source,
                isPrivate: true,
                cancellationToken);
        }
    }

    private static void AddLlvmSlotRoots(
        ICollection<CompilerInvocationCandidate> candidates,
        string parent,
        CancellationToken cancellationToken)
    {
        foreach (string directory in ReadDirectories(parent)
            .Where(path => Path.GetFileName(path).StartsWith(
                "llvm",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, SearchPaths.Comparer))
        {
            AddRoot(
                candidates,
                directory,
                Source.StandardPath,
                isPrivate: true,
                cancellationToken);
            foreach (string slot in ReadDirectories(directory)
                .Where(path => NumericName().IsMatch(Path.GetFileName(path)))
                .OrderBy(path => path, SearchPaths.Comparer))
            {
                AddRoot(
                    candidates,
                    slot,
                    Source.StandardPath,
                    isPrivate: true,
                    cancellationToken);
            }
        }
    }

    private static void AddRoot(
        ICollection<CompilerInvocationCandidate> candidates,
        string root,
        Source source,
        bool? isPrivate,
        CancellationToken cancellationToken,
        IEnumerable<CompilerSearchDirectory>? associatedDirectories = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullRoot;
        string resolvedRoot;
        try
        {
            fullRoot = Incant.Internal.FileSystemPath.Absolute(root);
            resolvedRoot = SearchPaths.Normalize(fullRoot);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (File.Exists(resolvedRoot))
        {
            string bin = Path.GetDirectoryName(fullRoot)!;
            candidates.Add(new CompilerInvocationCandidate(
                fullRoot,
                EnvironmentForBin(bin),
                source,
                CompilerDiscoveryAnchor.File,
                isPrivate ?? !IsSharedDirectory(bin),
                associatedDirectories));
            return;
        }

        if (!Directory.Exists(resolvedRoot))
        {
            return;
        }

        string[] directories = string.Equals(
            Path.GetFileName(fullRoot),
            "bin",
            StringComparison.OrdinalIgnoreCase)
            ? [fullRoot]
            : [fullRoot, Path.Combine(fullRoot, "bin")];
        foreach (string directory in directories)
        {
            bool privateDirectory = isPrivate ?? !IsSharedDirectory(directory);
            string environment = string.Equals(
                Path.GetFileName(directory),
                "bin",
                StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(directory)!
                : fullRoot;
            foreach (string path in ReadFiles(directory)
                .Where(path => CompilerName.Parse(path) is not null)
                .OrderBy(path => path, SearchPaths.Comparer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidates.Add(new CompilerInvocationCandidate(
                    path,
                    environment,
                    source,
                    CompilerDiscoveryAnchor.Directory,
                    privateDirectory,
                    associatedDirectories));
            }
        }
    }

    private static IReadOnlyList<string> ReadDirectories(string path)
    {
        try
        {
            return SearchPaths.Directories(SearchPaths.Normalize(path))
                .Select(directory => Path.Combine(path, Path.GetFileName(directory))).ToArray();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ReadFiles(string path)
    {
        try
        {
            return SearchPaths.Files(SearchPaths.Normalize(path))
                .Select(file => Path.Combine(path, Path.GetFileName(file))).ToArray();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string EnvironmentForBin(string bin) =>
        string.Equals(
            Path.GetFileName(bin),
            "bin",
            StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(bin)!
            : bin;

    private static bool IsCompilerFormula(string name) =>
        name.Equals("llvm", StringComparison.OrdinalIgnoreCase)
        || name.Equals("gcc", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("llvm@", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("gcc@", StringComparison.OrdinalIgnoreCase);

    private static bool IsHomebrewCommonBin(string unixPath) =>
        unixPath.EndsWith("/.linuxbrew/bin", StringComparison.Ordinal)
        || unixPath is "/opt/homebrew/bin" or "/usr/local/bin";

    [GeneratedRegex(@"^\d+(?:\.\d+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericName();
}
