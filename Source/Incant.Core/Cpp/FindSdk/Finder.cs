using Incant.Core.Cpp;

namespace Incant.Core.Cpp.FindSdk;

/// <summary>Discovers SDK components independently of toolsets, without pairing, caching or build-policy decisions.</summary>
public sealed class Finder
{
    /// <summary>Copies the supplied provider list.</summary>
    /// <param name="providers">Independent providers whose order does not determine completion order.</param>
    /// <exception cref="ArgumentException">A provider or its required metadata is invalid.</exception>
    /// <exception cref="ArgumentNullException">The provider collection is null.</exception>
    public Finder(IEnumerable<IDiscoveryProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        IDiscoveryProvider[] snapshot = providers.ToArray();
        if (snapshot.Any(provider => provider is null || string.IsNullOrWhiteSpace(provider.Name) || provider.Kinds is null || provider.Kinds.Count == 0))
        {
            throw new ArgumentException("Every provider needs a name and supported kinds.", nameof(providers));
        }

        Providers = Array.AsReadOnly(snapshot);
    }

    /// <summary>Gets the registered SDK providers.</summary>
    public IReadOnlyList<IDiscoveryProvider> Providers { get; }

    /// <summary>Creates independent built-in SDK providers.</summary>
    public static Finder CreateDefault() => new(
        [new WindowsProvider(), new AppleProvider(), new SystemProvider(), new BundleProvider(), new CompilerProvider()]);

    /// <summary>Performs a fresh parallel search. Invalid explicit inputs throw; normal absence returns an empty snapshot.</summary>
    /// <param name="query">Search constraints; null uses the default query.</param>
    /// <param name="cancellationToken">Cancels discovery and its probes.</param>
    /// <returns>A fresh immutable snapshot, including partial installations and diagnostics.</returns>
    /// <exception cref="ArgumentException">A query value or explicit path syntax is invalid.</exception>
    /// <exception cref="DiscoveryException">An explicit installation, compiler or sysroot input cannot be resolved.</exception>
    /// <exception cref="OperationCanceledException">Discovery is canceled.</exception>
    public async Task<DiscoveryResult> FindSdksAsync(SdkQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new SdkQuery();
        if (query.Kind is Kind kind && !Enum.IsDefined(kind)
            || query.TargetPlatform is TargetPlatform platform && !Enum.IsDefined(platform)
            || query.TargetArchitecture is TargetArchitecture architecture && !Enum.IsDefined(architecture)
            || query.AndroidApi is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }

        if (query.TargetTriple is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(query.TargetTriple);
        }

        if (query.Multilib is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(query.Multilib);
        }

        foreach (string? path in new[] { query.RootPath, query.CompilerPath, query.SysrootPath })
        {
            if (path is not null)
            {
                _ = Sdk.Absolute(path);
            }
        }

        var context = new DiscoveryContext(query.Environment, query.ProbeTimeout);
        SdkQuery snapshot = query with { Environment = context.Environment };
        cancellationToken.ThrowIfCancellationRequested();
        DiscoveryResult[] results = await Task.WhenAll(Providers
            .Where(provider => snapshot.Kind is null || provider.Kinds.Contains(snapshot.Kind.Value))
            .Select(provider => Task.Run(() => RunProviderAsync(provider, snapshot, context, cancellationToken), cancellationToken)))
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Sdk[] candidates = results.SelectMany(result => result.Sdks)
            .Where(sdk => snapshot.Kind is null || sdk.Kind == snapshot.Kind).ToArray();
        Diagnostic[] diagnostics = results.SelectMany(result => result.Diagnostics)
            .Concat(candidates.SelectMany(sdk => sdk.Diagnostics.Concat(sdk.Layouts.SelectMany(layout => layout.Diagnostics)))).Distinct().ToArray();
        if (snapshot.RootPath is not null)
        {
            string root = SearchPaths.Normalize(snapshot.RootPath);
            candidates = candidates.Where(sdk => SearchPaths.Related(root, SearchPaths.Normalize(sdk.RootPath))
                || SearchPaths.Related(root, SearchPaths.Normalize(sdk.EnvironmentPath))
                || sdk.Layouts.Any(layout => layout.SysrootPath is not null
                    && SearchPaths.Related(root, SearchPaths.Normalize(layout.SysrootPath)))).ToArray();
            if (candidates.Length == 0 && !results.SelectMany(result => result.RecognizedInputs)
                .Any(path => SearchPaths.Comparer.Equals(root, SearchPaths.Normalize(path))))
            {
                throw new DiscoveryException($"The explicit SDK path '{root}' did not resolve to an installation.", diagnostics);
            }
        }

        if (snapshot.CompilerPath is not null)
        {
            string compiler = SearchPaths.Normalize(snapshot.CompilerPath);
            candidates = candidates.Where(sdk => sdk.CompilerPath is not null
                && SearchPaths.Comparer.Equals(compiler, SearchPaths.Normalize(sdk.CompilerPath))).ToArray();
            if (candidates.Length == 0 && !results.SelectMany(result => result.RecognizedInputs)
                .Any(path => SearchPaths.Comparer.Equals(compiler, SearchPaths.Normalize(path))))
            {
                throw new DiscoveryException($"The explicit compiler '{compiler}' did not provide the requested SDK information.", diagnostics);
            }
        }

        Sdk[] selected = candidates
            .Where(sdk => snapshot.IncludePreview || sdk.Channel is Channel.Stable or Channel.Unknown)
            .Where(sdk => snapshot.Version?.Matches(sdk.Version) is not false
                && snapshot.ProductVersion?.Matches(sdk.ProductVersion) is not false)
            .Select(sdk => sdk.WithLayouts(sdk.Layouts.Where(layout => Matches(layout, snapshot)), sdk.Sources))
            .Where(sdk => sdk.Layouts.Count > 0 || !HasTargetConstraint(snapshot))
            .GroupBy(sdk => (sdk.Kind, Root: PathKey(sdk.RootPath), sdk.Version,
                Compiler: sdk.CompilerPath is null ? null : PathKey(sdk.CompilerPath)))
            .Select(group =>
            {
                Sdk preferred = group.OrderBy(sdk => Priority(sdk.Sources)).First();
                return preferred.WithLayouts(MergeLayouts(group.OrderBy(sdk => Priority(sdk.Sources)).SelectMany(sdk => sdk.Layouts)),
                    group.SelectMany(sdk => sdk.Sources).Distinct().Order(), group.SelectMany(sdk => sdk.Diagnostics).Distinct());
            })
            .OrderBy(sdk => Priority(sdk.Sources))
            .ThenBy(sdk => sdk.Channel == Channel.Stable ? 0 : 1)
            .ThenByDescending(sdk => sdk.Version)
            .ThenBy(sdk => sdk.RootPath, SearchPaths.Comparer).ToArray();
        if (selected.Length == 0 && candidates.Length > 0)
        {
            diagnostics = diagnostics.Append(new Diagnostic(DiagnosticSeverity.Info, "no-matching-sdk", nameof(Finder),
                "Recognized SDK installations do not satisfy the requested version, channel or target constraints.",
                snapshot.RootPath ?? snapshot.CompilerPath)).ToArray();
        }

        return new DiscoveryResult(selected, diagnostics);
    }

    /// <summary>Returns the preferred matching SDK component, or null.</summary>
    /// <param name="query">The SDK search constraints.</param>
    /// <param name="cancellationToken">Cancels discovery.</param>
    /// <returns>The preferred matching SDK, which may be partial, or null when none matches.</returns>
    /// <exception cref="ArgumentException">The query is null or contains an invalid value.</exception>
    /// <exception cref="DiscoveryException">An explicit discovery input cannot be resolved.</exception>
    /// <exception cref="OperationCanceledException">Discovery is canceled.</exception>
    public async Task<Sdk?> FindSdkAsync(SdkQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return (await FindSdksAsync(query, cancellationToken).ConfigureAwait(false)).Sdks.FirstOrDefault();
    }

    /// <summary>Synchronously waits for <see cref="FindSdksAsync"/>.</summary>
    /// <inheritdoc cref="FindSdksAsync"/>
    public DiscoveryResult FindSdks(SdkQuery? query = null, CancellationToken cancellationToken = default) =>
        FindSdksAsync(query, cancellationToken).GetAwaiter().GetResult();

    /// <summary>Synchronously waits for <see cref="FindSdkAsync"/>.</summary>
    /// <inheritdoc cref="FindSdkAsync"/>
    public Sdk? FindSdk(SdkQuery query, CancellationToken cancellationToken = default) =>
        FindSdkAsync(query, cancellationToken).GetAwaiter().GetResult();

    private static IEnumerable<TargetLayout> MergeLayouts(IEnumerable<TargetLayout> layouts) => layouts
        .GroupBy(layout => (layout.Platform, layout.Architecture,
            Triple: layout.TargetTriple is null ? null : new TargetIdentity(layout.TargetTriple).Canonical,
            layout.SysrootPath, layout.Multilib))
        .Select(group =>
        {
            TargetLayout preferred = group.First();
            return new TargetLayout(preferred.Platform, preferred.Architecture, preferred.Resources,
                preferred.TargetTriple, preferred.SysrootPath, preferred.ApiLevels, preferred.ApiAliases,
                preferred.Multilib, preferred.MinimumDeploymentVersion, preferred.DefaultDeploymentVersion,
                group.SelectMany(layout => layout.Diagnostics).Distinct());
        });

    private static bool Matches(TargetLayout layout, SdkQuery query) =>
        (query.TargetPlatform is null || layout.Platform == query.TargetPlatform)
        && (query.TargetArchitecture is null || layout.Architecture == query.TargetArchitecture)
        && (query.TargetTriple is null || TargetIdentity.AreEquivalent(layout.TargetTriple, query.TargetTriple))
        && (query.Multilib is null || string.Equals(layout.Multilib, query.Multilib, StringComparison.Ordinal))
        && (query.AndroidApi is null || layout.ApiLevels.Contains(query.AndroidApi.Value)
            || layout.ApiAliases.ContainsKey(query.AndroidApi.Value));

    private static bool HasTargetConstraint(SdkQuery query) =>
        query.TargetPlatform is not null || query.TargetArchitecture is not null
        || query.TargetTriple is not null || query.AndroidApi is not null || query.Multilib is not null;

    private static int Priority(IReadOnlyList<Source> sources) => sources.Count == 0 ? int.MaxValue : (int)sources.Min();

    private static string PathKey(string path)
    {
        string normalized = SearchPaths.Normalize(path);
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static async Task<DiscoveryResult> RunProviderAsync(
        IDiscoveryProvider provider, SdkQuery query, DiscoveryContext context, CancellationToken cancellationToken)
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
        catch (DiscoveryException) when (query.RootPath is not null || query.CompilerPath is not null || query.SysrootPath is not null)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Providers are an extension boundary; automatic failures become visible diagnostics.
            return new DiscoveryResult(diagnostics: [new Diagnostic(DiagnosticSeverity.Error,
                "provider-failed", provider.Name, exception.Message)]);
        }
    }
}

/// <summary>Describes one uncached SDK search; compiler and sysroot paths are independent of toolset objects.</summary>
public sealed record SdkQuery
{
    /// <summary>Gets the component family, or null for all providers.</summary>
    public Kind? Kind { get; init; }

    /// <summary>Gets an explicit SDK or developer environment root. It restricts discovery to that installation.</summary>
    public string? RootPath { get; init; }

    /// <summary>Gets an explicit compiler invocation entry for development-file or compiler-reported sysroot queries.</summary>
    public string? CompilerPath { get; init; }

    /// <summary>Gets an explicitly selected sysroot for target-aware compiler queries.</summary>
    public string? SysrootPath { get; init; }

    /// <summary>Gets the SDK's own version requirement.</summary>
    public VersionConstraint? Version { get; init; }

    /// <summary>Gets an optional enclosing product version requirement.</summary>
    public VersionConstraint? ProductVersion { get; init; }

    /// <summary>Gets the requested target platform.</summary>
    public TargetPlatform? TargetPlatform { get; init; }

    /// <summary>Gets the requested target architecture.</summary>
    public TargetArchitecture? TargetArchitecture { get; init; }

    /// <summary>Gets a specific compiler target triple, not arbitrary compiler flags.</summary>
    public string? TargetTriple { get; init; }

    /// <summary>Gets an installed SDK-local library variant identifier. Null accepts all variants; a dot selects the default classified variant. This is not a path or compiler argument.</summary>
    public string? Multilib { get; init; }

    /// <summary>Gets an Android API requirement, evaluated separately for each ABI.</summary>
    public int? AndroidApi { get; init; }

    /// <summary>Gets whether preview and experimental installations are eligible.</summary>
    public bool IncludePreview { get; init; }

    /// <summary>Gets a complete replacement environment; null captures the current environment.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Gets the finite timeout for each read-only probe.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Discovers platform or compiler SDK components independently of executable toolset selection.</summary>
public interface IDiscoveryProvider
{
    /// <summary>Gets the provider's diagnostic name.</summary>
    string Name { get; }

    /// <summary>Gets the SDK component families handled by this provider.</summary>
    IReadOnlyCollection<Kind> Kinds { get; }

    /// <summary>Returns independent component snapshots; the Finder performs final filtering and ordering.</summary>
    /// <param name="query">Immutable search constraints.</param>
    /// <param name="context">The captured discovery environment and bounded probe runner.</param>
    /// <param name="cancellationToken">Cancels this provider's work.</param>
    /// <returns>Independent component snapshots and recoverable diagnostics.</returns>
    /// <exception cref="DiscoveryException">A required explicit input cannot be resolved.</exception>
    /// <exception cref="OperationCanceledException">Discovery is canceled.</exception>
    Task<DiscoveryResult> DiscoverAsync(SdkQuery query, DiscoveryContext context, CancellationToken cancellationToken);
}

/// <summary>An immutable SDK search result; platform and compiler components are never implicitly merged.</summary>
public sealed class DiscoveryResult
{
    /// <summary>Copies SDK and diagnostic collections.</summary>
    /// <param name="sdks">SDK snapshots, copied without changing their identity.</param>
    /// <param name="diagnostics">Discovery observations.</param>
    /// <exception cref="ArgumentException">A collection contains null.</exception>
    public DiscoveryResult(IEnumerable<Sdk>? sdks = null, IEnumerable<Diagnostic>? diagnostics = null)
        : this(sdks, diagnostics, [])
    {
    }

    private DiscoveryResult(IEnumerable<Sdk>? sdks, IEnumerable<Diagnostic>? diagnostics, IEnumerable<string> recognizedInputs)
    {
        Sdks = SearchPaths.Freeze(sdks);
        Diagnostics = SearchPaths.Freeze(diagnostics);
        RecognizedInputs = SearchPaths.Freeze(recognizedInputs);
        if (Sdks.Any(sdk => sdk is null))
        {
            throw new ArgumentException("SDKs cannot contain null.", nameof(sdks));
        }

        if (Diagnostics.Any(diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Diagnostics cannot contain null.", nameof(diagnostics));
        }
    }

    // Recognized explicit inputs can provide no layouts for a requested target without being invalid installations.
    internal IReadOnlyList<string> RecognizedInputs { get; }

    internal DiscoveryResult WithRecognizedInputs(IEnumerable<string> paths) =>
        new(Sdks, Diagnostics, RecognizedInputs.Concat(paths).Distinct(SearchPaths.Comparer));

    /// <summary>Gets discovered SDK components.</summary>
    public IReadOnlyList<Sdk> Sdks { get; }

    /// <summary>Gets discovery failures and observations.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}
