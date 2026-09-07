namespace Incant.Core.Cpp;

internal enum CompilerFamily
{
    Gnu,
    Llvm,
    AppleClang,
}

internal sealed record CompilerSearchDirectory(string Path, bool IsPrivate);

internal sealed record CompilerInspectionFailure(
    string Path,
    string Message,
    IReadOnlyList<Source> Sources);

internal sealed record CompilerDiscoveryResult(
    IReadOnlyList<CompilerInstallation> Installations,
    IReadOnlyList<CompilerInspectionFailure> Failures);

internal sealed class CompilerInstallation
{
    private CompilerInstallation(
        CompilerInvocationCandidate primary,
        CompilerProbe probe,
        CompilerFamily family,
        IReadOnlyList<string> aliases,
        IReadOnlyList<CompilerSearchDirectory> searchDirectories,
        IReadOnlyList<Source> sources)
    {
        InvocationPath = primary.InvocationPath;
        CanonicalPath = primary.CanonicalPath;
        EnvironmentPath = primary.EnvironmentPath;
        Probe = probe;
        Family = family;
        Aliases = aliases;
        SearchDirectories = searchDirectories;
        Sources = sources;
    }

    internal string InvocationPath { get; }

    internal string CanonicalPath { get; }

    internal string BinPath => Path.GetDirectoryName(InvocationPath)!;

    internal string EnvironmentPath { get; }

    internal CompilerProbe Probe { get; }

    internal CompilerFamily Family { get; }

    internal Version? Version => Probe.Version;

    internal string? DefaultTargetTriple => Probe.DefaultTarget?.Triple;

    internal Channel Channel => SearchPaths.Channel(Probe.IdentityText);

    internal IReadOnlyList<string> Aliases { get; }

    internal IReadOnlyList<CompilerSearchDirectory> SearchDirectories { get; }

    internal IReadOnlyList<Source> Sources { get; }

    internal static async Task<CompilerDiscoveryResult> DiscoverAsync(
        string? explicitRoot,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CompilerInvocationCandidate> candidates =
            await CompilerLocator.FindAsync(
                explicitRoot,
                context,
                cancellationToken).ConfigureAwait(false);
        Task<CompilerInspection>[] tasks = candidates
            .Select(candidate => InspectAsync(
                candidate,
                context,
                cancellationToken))
            .ToArray();
        CompilerInspection[] inspections =
            await Task.WhenAll(tasks).ConfigureAwait(false);
        CompilerInspectionFailure[] failures = inspections
            .Where(inspection => inspection.Failure is not null)
            .Select(inspection => inspection.Failure!)
            .ToArray();
        CompilerInstallation[] installations = inspections
            .Where(inspection => inspection.Probe is not null)
            .GroupBy(inspection => Identity(inspection.Candidate, inspection.Probe!))
            .Select(Create)
            .ToArray();
        return new CompilerDiscoveryResult(installations, failures);
    }

    private static async Task<CompilerInspection> InspectAsync(
        CompilerInvocationCandidate candidate,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            CompilerProbe? probe = await CompilerProbe.OpenAsync(
                candidate.InvocationPath,
                context,
                cancellationToken).ConfigureAwait(false);
            if (probe is null)
            {
                return new CompilerInspection(
                    candidate,
                    null,
                    new CompilerInspectionFailure(
                        candidate.InvocationPath,
                        "The compiler identity could not be established.",
                        candidate.Sources));
            }

            if (probe.DefaultTarget?.Triple.Contains(
                "mingw",
                StringComparison.OrdinalIgnoreCase) == true)
            {
                return new CompilerInspection(candidate, null, null);
            }

            return new CompilerInspection(candidate, probe, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return new CompilerInspection(
                candidate,
                null,
                new CompilerInspectionFailure(
                    candidate.InvocationPath,
                    exception.Message,
                    candidate.Sources));
        }
    }

    private static CompilerInstallation Create(
        IGrouping<CompilerInstallationIdentity, CompilerInspection> group)
    {
        CompilerInspection[] inspections = group.ToArray();
        IOrderedEnumerable<CompilerInspection> ordered = inspections
            .OrderBy(inspection => inspection.Candidate.Sources.Min())
            .ThenBy(inspection => inspection.Candidate.DiscoveryAnchor)
            .ThenBy(inspection => DriverRank(inspection.Candidate.InvocationPath))
            .ThenBy(inspection => InvocationRank(
                inspection.Candidate.InvocationPath))
            .ThenBy(
                inspection => inspection.Candidate.InvocationPath,
                SearchPaths.Comparer);
        CompilerInspection preferred = ordered.First();
        var aliases = new List<string>();
        var searchDirectories = new List<CompilerSearchDirectory>();
        foreach (CompilerInspection inspection in ordered)
        {
            AddPath(aliases, inspection.Candidate.InvocationPath);
            string invocationDirectory = Path.GetDirectoryName(
                inspection.Candidate.InvocationPath)!;
            AddDirectory(
                searchDirectories,
                invocationDirectory,
                inspection.Candidate.IsPrivateDirectory);
            string canonicalDirectory = Path.GetDirectoryName(
                inspection.Candidate.CanonicalPath)!;
            AddDirectory(
                searchDirectories,
                canonicalDirectory,
                !CompilerLocator.IsSharedDirectory(canonicalDirectory));
            foreach (CompilerSearchDirectory directory
                in inspection.Candidate.AssociatedSearchDirectories)
            {
                AddDirectory(
                    searchDirectories,
                    directory.Path,
                    directory.IsPrivate);
            }
        }

        return new CompilerInstallation(
            preferred.Candidate,
            preferred.Probe!,
            group.Key.Family,
            aliases.ToArray(),
            searchDirectories.ToArray(),
            inspections
                .SelectMany(inspection => inspection.Candidate.Sources)
                .Distinct()
                .Order()
                .ToArray());
    }

    private static CompilerInstallationIdentity Identity(
        CompilerInvocationCandidate candidate,
        CompilerProbe probe)
    {
        CompilerFamily family = probe.IsApple
            ? CompilerFamily.AppleClang
            : probe.IsClang
                ? CompilerFamily.Llvm
                : CompilerFamily.Gnu;
        string canonicalDirectory = Path.GetDirectoryName(
            candidate.CanonicalPath)!;
        string pathKey = OperatingSystem.IsWindows()
            ? canonicalDirectory.ToUpperInvariant()
            : canonicalDirectory;
        return new CompilerInstallationIdentity(
            family,
            pathKey,
            probe.Version,
            probe.DefaultTarget?.Canonical);
    }

    private static int DriverRank(string path)
    {
        string stem = CompilerLocator.ExecutableStem(path).ToLowerInvariant();
        return stem.Contains("++", StringComparison.Ordinal)
            ? 2
            : stem.Contains("clang-cl", StringComparison.Ordinal)
                ? 1
                : 0;
    }

    private static int InvocationRank(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Length == 0
            || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            ? 1
            : extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                ? 2
                : extension.Equals(".py", StringComparison.OrdinalIgnoreCase)
                    ? 3
                    : 4;
    }

    private static void AddPath(ICollection<string> paths, string path)
    {
        if (!paths.Contains(path, SearchPaths.Comparer))
        {
            paths.Add(path);
        }
    }

    private static void AddDirectory(
        IList<CompilerSearchDirectory> directories,
        string path,
        bool isPrivate)
    {
        int index = -1;
        for (int candidateIndex = 0;
            candidateIndex < directories.Count;
            candidateIndex++)
        {
            if (SearchPaths.Comparer.Equals(
                directories[candidateIndex].Path,
                path))
            {
                index = candidateIndex;
                break;
            }
        }
        if (index < 0)
        {
            directories.Add(new CompilerSearchDirectory(path, isPrivate));
        }
        else if (!isPrivate && directories[index].IsPrivate)
        {
            directories[index] =
                new CompilerSearchDirectory(path, IsPrivate: false);
        }
    }

    private sealed record CompilerInspection(
        CompilerInvocationCandidate Candidate,
        CompilerProbe? Probe,
        CompilerInspectionFailure? Failure);

    private sealed record CompilerInstallationIdentity(
        CompilerFamily Family,
        string CanonicalDirectory,
        Version? Version,
        string? DefaultTargetTriple);
}
