namespace Incant.Core.Cpp;

internal enum CompilerDiscoveryAnchor
{
    File,
    Directory,
}

internal sealed class CompilerInvocationCandidate
{
    internal CompilerInvocationCandidate(
        string invocationPath,
        string environmentPath,
        Source source,
        CompilerDiscoveryAnchor discoveryAnchor,
        bool isPrivateDirectory,
        IEnumerable<CompilerSearchDirectory>? associatedSearchDirectories = null)
        : this(
            invocationPath,
            environmentPath,
            [source],
            discoveryAnchor,
            isPrivateDirectory,
            associatedSearchDirectories)
    {
    }

    internal CompilerInvocationCandidate(
        string invocationPath,
        string environmentPath,
        IEnumerable<Source> sources,
        CompilerDiscoveryAnchor discoveryAnchor,
        bool isPrivateDirectory,
        IEnumerable<CompilerSearchDirectory>? associatedSearchDirectories = null)
    {
        InvocationPath = Path.GetFullPath(invocationPath);
        CanonicalPath = SearchPaths.InvocationIdentity(invocationPath);
        EnvironmentPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(environmentPath));
        Sources = SearchPaths.Freeze(sources.Distinct().Order());
        DiscoveryAnchor = discoveryAnchor;
        IsPrivateDirectory = isPrivateDirectory;
        AssociatedSearchDirectories = SearchPaths.Freeze(
            MergeDirectories(associatedSearchDirectories ?? []));
    }

    internal Version? ProductVersion { get; init; }

    internal Channel? ProductChannel { get; init; }

    internal string InvocationPath { get; }

    internal string CanonicalPath { get; }

    internal string EnvironmentPath { get; }

    internal IReadOnlyList<Source> Sources { get; }

    internal CompilerDiscoveryAnchor DiscoveryAnchor { get; }

    internal bool IsPrivateDirectory { get; }

    internal IReadOnlyList<CompilerSearchDirectory> AssociatedSearchDirectories { get; }

    internal static IReadOnlyList<CompilerInvocationCandidate> Merge(
        IEnumerable<CompilerInvocationCandidate> candidates) => candidates
        .GroupBy(candidate => candidate.InvocationPath, SearchPaths.Comparer)
        .Select(group =>
        {
            CompilerInvocationCandidate[] ordered = group
                .OrderBy(candidate => candidate.Sources.Min())
                .ThenBy(candidate => candidate.DiscoveryAnchor)
                .ToArray();
            CompilerInvocationCandidate preferred = ordered[0];
            return new CompilerInvocationCandidate(
                preferred.InvocationPath,
                preferred.EnvironmentPath,
                ordered.SelectMany(candidate => candidate.Sources),
                preferred.DiscoveryAnchor,
                ordered.All(candidate => candidate.IsPrivateDirectory),
                ordered.SelectMany(
                    candidate => candidate.AssociatedSearchDirectories))
            {
                ProductVersion = ordered.Select(candidate => candidate.ProductVersion).FirstOrDefault(value => value is not null),
                ProductChannel = ordered.Select(candidate => candidate.ProductChannel).FirstOrDefault(value => value is not null),
            };
        })
        .ToArray();

    private static IEnumerable<CompilerSearchDirectory> MergeDirectories(
        IEnumerable<CompilerSearchDirectory> directories) => directories
        .Select(directory => new CompilerSearchDirectory(
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(directory.Path)),
            directory.IsPrivate))
        .GroupBy(directory => directory.Path, SearchPaths.Comparer)
        .Select(group => new CompilerSearchDirectory(
            group.First().Path,
            group.All(directory => directory.IsPrivate)));
}
