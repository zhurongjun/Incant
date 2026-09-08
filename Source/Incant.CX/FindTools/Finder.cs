using Incant.CX;

namespace Incant.CX.FindTools;

/// <summary>Runs independent providers in parallel and selects toolsets without caching or SDK pairing.</summary>
public sealed class Finder
{
    /// <summary>Copies caller-supplied providers. Providers must expose a name and at least one kind.</summary>
    public Finder(IEnumerable<IDiscoveryProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        IDiscoveryProvider[] snapshot = providers.ToArray();
        if (snapshot.Any(provider => provider is null || string.IsNullOrWhiteSpace(provider.Name)
            || provider.Kinds is null || provider.Kinds.Count == 0))
        {
            throw new ArgumentException("Every provider needs a name and supported kinds.", nameof(providers));
        }

        Providers = Array.AsReadOnly(snapshot);
    }

    /// <summary>Gets the registered providers.</summary>
    public IReadOnlyList<IDiscoveryProvider> Providers { get; }

    /// <summary>Creates a Finder containing the built-in environment providers.</summary>
    public static Finder CreateDefault() => new(
        [new VisualStudioProvider(), new CompilerProvider(), new AppleProvider(), new BundleProvider()]);

    /// <summary>Scans on every call. Explicit invalid paths throw; an ordinary lack of matches returns an empty snapshot.</summary>
    public async Task<DiscoveryResult> FindToolSetsAsync(ToolSetQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new ToolSetQuery();
        if (query.Kind is Kind kind && !Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }

        var context = new DiscoveryContext(query.Environment, query.ProbeTimeout);
        ToolSetQuery snapshot = query with { Environment = context.Environment };
        cancellationToken.ThrowIfCancellationRequested();
        // The pool boundary also covers synchronous directory/registry work before a provider's first await.
        Task<DiscoveryResult>[] tasks = Providers.Where(provider => snapshot.Kind is null || provider.Kinds.Contains(snapshot.Kind.Value))
            .Select(provider => Task.Run(() => RunProviderAsync(provider, snapshot, context, cancellationToken), cancellationToken)).ToArray();
        DiscoveryResult[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ToolSet[] candidates = results.SelectMany(result => result.ToolSets)
            .Where(candidate => snapshot.Kind is null || candidate.Kind == snapshot.Kind).ToArray();
        Diagnostic[] diagnostics = results.SelectMany(result => result.Diagnostics)
            .Concat(candidates.SelectMany(candidate => candidate.Diagnostics)).Distinct().ToArray();

        if (snapshot.RootPath is not null)
        {
            string root = SearchPaths.Normalize(snapshot.RootPath);
            candidates = candidates.Where(candidate => SearchPaths.Related(root, SearchPaths.Normalize(candidate.RootPath))
                || candidate.CompilerPath is not null && SearchPaths.Comparer.Equals(root, SearchPaths.Normalize(candidate.CompilerPath))).ToArray();
            if (candidates.Length == 0
                && !results.SelectMany(result => result.RecognizedInputs)
                    .Any(path => SearchPaths.Comparer.Equals(
                        root,
                        SearchPaths.Normalize(path))))
            {
                throw new DiscoveryException($"The explicit tool path '{root}' did not resolve to an installation.", diagnostics);
            }
        }

        ToolSet[] selected = candidates
            .Where(candidate => snapshot.IncludePreview || candidate.Channel is Channel.Stable or Channel.Unknown)
            .Where(candidate => snapshot.Version?.Matches(candidate.Version) is not false
                && snapshot.ProductVersion?.Matches(candidate.ProductVersion) is not false
                && snapshot.CompilerVersion?.Matches(candidate.CompilerVersion) is not false)
            .GroupBy(candidate => (candidate.Kind,
                Root: SearchPaths.PathKey(candidate.RootPath),
                Compiler: candidate.CompilerPath is null ? null : SearchPaths.PathKey(candidate.CompilerPath),
                candidate.Version, candidate.DefaultTargetTriple))
            .Select(group => group.OrderBy(candidate => SearchPaths.SourcePriority(candidate.Sources)).First()
                .WithSources(group.SelectMany(candidate => candidate.Sources).Distinct().Order()))
            .OrderBy(candidate => SearchPaths.SourcePriority(candidate.Sources))
            .ThenBy(candidate => candidate.Channel == Channel.Stable ? 0 : 1)
            .ThenByDescending(candidate => candidate.Version)
            .ThenByDescending(candidate => candidate.ProductVersion)
            .ThenBy(candidate => candidate.RootPath, SearchPaths.Comparer)
            .ToArray();
        if (selected.Length == 0 && candidates.Length > 0)
        {
            diagnostics = diagnostics.Append(new Diagnostic(
                DiagnosticSeverity.Info,
                "no-matching-toolset",
                nameof(Finder),
                "Recognized toolset installations do not satisfy the requested version or channel constraints.",
                snapshot.RootPath)).ToArray();
        }

        return new DiscoveryResult(selected, diagnostics);
    }

    /// <summary>Returns the preferred matching toolset, or null.</summary>
    public async Task<ToolSet?> FindToolSetAsync(ToolSetQuery? query = null, CancellationToken cancellationToken = default) =>
        (await FindToolSetsAsync(query, cancellationToken).ConfigureAwait(false)).ToolSets.FirstOrDefault();

    /// <summary>Synchronously waits for <see cref="FindToolSetsAsync"/>.</summary>
    public DiscoveryResult FindToolSets(ToolSetQuery? query = null, CancellationToken cancellationToken = default) =>
        FindToolSetsAsync(query, cancellationToken).GetAwaiter().GetResult();

    /// <summary>Synchronously waits for <see cref="FindToolSetAsync"/>.</summary>
    public ToolSet? FindToolSet(ToolSetQuery? query = null, CancellationToken cancellationToken = default) =>
        FindToolSetAsync(query, cancellationToken).GetAwaiter().GetResult();

    private static async Task<DiscoveryResult> RunProviderAsync(
        IDiscoveryProvider provider, ToolSetQuery query, DiscoveryContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.DiscoverAsync(query, context, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The provider returned no discovery result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Providers are an extension boundary: one failure must not hide the remaining environments.
            return new DiscoveryResult(diagnostics: [new Diagnostic(DiagnosticSeverity.Error,
                "provider-failed", provider.Name, exception.Message)]);
        }
    }
}

/// <summary>Controls one fresh search. An explicit root restricts discovery to that environment.</summary>
public sealed record ToolSetQuery
{
    /// <summary>Gets the requested installation family, or null for every provider.</summary>
    public Kind? Kind { get; init; }

    /// <summary>Gets an explicit installation directory or compiler path. Invalid roots raise DiscoveryException.</summary>
    public string? RootPath { get; init; }

    /// <summary>Gets the concrete toolset version requirement.</summary>
    public VersionConstraint? Version { get; init; }

    /// <summary>Gets the enclosing product version requirement.</summary>
    public VersionConstraint? ProductVersion { get; init; }

    /// <summary>Gets the compiler version requirement.</summary>
    public VersionConstraint? CompilerVersion { get; init; }

    /// <summary>Gets whether preview and experimental versions are eligible.</summary>
    public bool IncludePreview { get; init; }

    /// <summary>Gets a complete replacement environment; null captures the current process environment.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Gets the positive finite timeout for each probe.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Discovers concrete-version tool environments, without resolving every tool or pairing SDKs.</summary>
public interface IDiscoveryProvider
{
    /// <summary>Gets the provider name used in diagnostics.</summary>
    string Name { get; }

    /// <summary>Gets installation families supported by this provider.</summary>
    IReadOnlyCollection<Kind> Kinds { get; }

    /// <summary>Discovers candidates in the captured environment. The Finder performs final filtering and ordering.</summary>
    Task<DiscoveryResult> DiscoverAsync(ToolSetQuery query, DiscoveryContext context, CancellationToken cancellationToken);
}

/// <summary>An immutable toolset discovery snapshot, including failed automatic candidates.</summary>
public sealed class DiscoveryResult
{
    /// <summary>Copies the supplied collections; later caller mutations cannot change this snapshot.</summary>
    public DiscoveryResult(
        IEnumerable<ToolSet>? toolSets = null,
        IEnumerable<Diagnostic>? diagnostics = null)
        : this(toolSets, diagnostics, [])
    {
    }

    private DiscoveryResult(
        IEnumerable<ToolSet>? toolSets,
        IEnumerable<Diagnostic>? diagnostics,
        IEnumerable<string> recognizedInputs)
    {
        ToolSets = SearchPaths.Freeze(toolSets);
        Diagnostics = SearchPaths.Freeze(diagnostics);
        RecognizedInputs = SearchPaths.Freeze(recognizedInputs);
    }

    // A recognized explicit compiler can belong to another requested family.
    internal IReadOnlyList<string> RecognizedInputs { get; }

    internal DiscoveryResult WithRecognizedInputs(
        IEnumerable<string> paths) => new(
            ToolSets,
            Diagnostics,
            RecognizedInputs.Concat(paths).Distinct(SearchPaths.Comparer));

    /// <summary>Gets discovered environments in selection order when returned by a Finder.</summary>
    public IReadOnlyList<ToolSet> ToolSets { get; }

    /// <summary>Gets diagnostics, including provider failures.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}
