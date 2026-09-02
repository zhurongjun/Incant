using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Incant.Base;

namespace Incant.Core.Toolchains;

/// <summary>Coordinates provider discovery, normalization, caching, and profile construction.</summary>
public sealed class DiscoveryService
{
    private readonly IReadOnlyList<IDiscoveryProvider> _providers;
    private readonly ConcurrentDictionary<string, Task<Catalog>> _cache = new(StringComparer.Ordinal);

    /// <summary>Initializes a discovery service with caller-supplied providers.</summary>
    /// <param name="providers">The providers owned by the new service.</param>
    /// <exception cref="ArgumentNullException"><paramref name="providers"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The collection contains a null provider, or a provider has no name or supported kind.
    /// </exception>
    public DiscoveryService(IEnumerable<IDiscoveryProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        IDiscoveryProvider[] providerArray = providers.ToArray();
        if (providerArray.Any(provider => provider is null))
        {
            throw new ArgumentException("The provider collection cannot contain null values.", nameof(providers));
        }

        if (providerArray.Any(provider =>
            string.IsNullOrWhiteSpace(provider.Name)
            || provider.Kinds is null
            || provider.Kinds.Count == 0))
        {
            throw new ArgumentException(
                "Every provider must expose a name and at least one supported kind.",
                nameof(providers));
        }

        _providers = Array.AsReadOnly(providerArray);
    }

    /// <summary>Creates a service containing every built-in provider.</summary>
    /// <returns>A discovery service containing the built-in providers.</returns>
    public static DiscoveryService CreateDefault() => new(
    [
        new VisualStudioDiscoveryProvider(),
        new WindowsSdkDiscoveryProvider(),
        new GnuDiscoveryProvider(),
        new LlvmDiscoveryProvider(),
        new XcodeDiscoveryProvider(),
        new AndroidNdkDiscoveryProvider(),
        new EmscriptenDiscoveryProvider(),
        new WasiSdkDiscoveryProvider(),
    ]);

    /// <summary>Gets the providers owned by this service.</summary>
    public IReadOnlyList<IDiscoveryProvider> Providers => _providers;

    /// <summary>Removes all cached discovery snapshots from this service.</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>Discovers installations and SDKs using one immutable environment snapshot.</summary>
    /// <param name="options">Discovery filters and path hints.</param>
    /// <param name="cancellationToken">A token that cancels uncached discovery.</param>
    /// <returns>An immutable catalog snapshot.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The probe timeout is not positive.</exception>
    /// <exception cref="ArgumentException">The kind filter contains an unknown value.</exception>
    /// <exception cref="DiscoveryException">An explicit path cannot be resolved.</exception>
    public async Task<Catalog> DiscoverAsync(
        DiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        DiscoveryOptions effectiveOptions = ValidateOptions(options);
        IReadOnlyDictionary<string, string?> environment = ProviderUtilities.CaptureEnvironment(
            effectiveOptions.Environment);
        var context = new DiscoveryContext(effectiveOptions, environment);

        if (cancellationToken.CanBeCanceled)
        {
            return await DiscoverCoreAsync(context, cancellationToken).ConfigureAwait(false);
        }

        string cacheKey = CreateCacheKey(effectiveOptions, environment);
        if (effectiveOptions.Refresh)
        {
            _cache.TryRemove(cacheKey, out _);
        }

        Task<Catalog> task = _cache.GetOrAdd(
            cacheKey,
            _ => DiscoverCoreAsync(context, CancellationToken.None));
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            _cache.TryRemove(new KeyValuePair<string, Task<Catalog>>(cacheKey, task));
            throw;
        }
    }

    private static DiscoveryOptions ValidateOptions(DiscoveryOptions? options)
    {
        DiscoveryOptions suppliedOptions = options ?? new DiscoveryOptions();
        DiscoveryOptions effectiveOptions = new()
        {
            Kinds = suppliedOptions.Kinds is null
                ? null
                : Array.AsReadOnly(suppliedOptions.Kinds.Distinct().ToArray()),
            ExplicitPaths = suppliedOptions.ExplicitPaths is null
                ? null
                : Array.AsReadOnly(suppliedOptions.ExplicitPaths.ToArray()),
            IncludePreview = suppliedOptions.IncludePreview,
            Refresh = suppliedOptions.Refresh,
            Environment = suppliedOptions.Environment is null
                ? null
                : new ReadOnlyDictionary<string, string?>(
                    new Dictionary<string, string?>(suppliedOptions.Environment)),
            ProbeTimeout = suppliedOptions.ProbeTimeout,
        };
        if (effectiveOptions.ProbeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                effectiveOptions.ProbeTimeout,
                "The probe timeout must be positive.");
        }

        if (effectiveOptions.Kinds?.Any(kind => !Enum.IsDefined(kind)) == true)
        {
            throw new ArgumentException("The kind filter contains an unknown value.", nameof(options));
        }

        return effectiveOptions;
    }

    private async Task<Catalog> DiscoverCoreAsync(
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        HashSet<Kind>? kinds = context.Options.Kinds is null
            ? null
            : new HashSet<Kind>(context.Options.Kinds);
        IDiscoveryProvider[] selectedProviders = _providers
            .Where(provider => kinds is null || provider.Kinds.Any(kinds.Contains))
            .ToArray();

        Task<DiscoveryResult>[] tasks = selectedProviders
            .Select(provider => RunProviderAsync(provider, context, cancellationToken))
            .ToArray();
        DiscoveryResult[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        List<Installation> installations = DeduplicateInstallations(
            results.SelectMany(result => result.Installations),
            context.Options.IncludePreview);
        List<SdkInstallation> sdks = DeduplicateSdks(results.SelectMany(result => result.Sdks));
        RemoveUnpairedSdks(sdks, installations);
        List<Diagnostic> diagnostics = results.SelectMany(result => result.Diagnostics).ToList();

        ValidateExplicitPaths(context.Options.ExplicitPaths, installations, sdks, diagnostics);
        List<Profile> profiles = BuildProfiles(installations, sdks);
        return new Catalog(installations, sdks, profiles, diagnostics);
    }

    private static async Task<DiscoveryResult> RunProviderAsync(
        IDiscoveryProvider provider,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.DiscoverAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new DiscoveryResult(
                diagnostics:
                [
                    new Diagnostic(
                        DiagnosticSeverity.Error,
                        "provider-failed",
                        provider.Name,
                        exception.Message),
                ]);
        }
    }

    private static List<Installation> DeduplicateInstallations(
        IEnumerable<Installation> candidates,
        bool includePreview)
    {
        StringComparer pathComparer = ProviderUtilities.GetPathComparer();
        return candidates
            .Where(candidate => includePreview || candidate.Channel == Channel.Stable)
            .GroupBy(
                candidate => (candidate.Kind, candidate.RootPath),
                new InstallationPathKeyComparer(pathComparer))
            .SelectMany(SplitAndMergeInstallations)
            .OrderBy(candidate => Resolver.GetSourcePriority(candidate.Sources.First()))
            .ThenBy(candidate => candidate.Kind)
            .ThenByDescending(candidate => candidate.ProductVersion)
            .ThenByDescending(candidate => candidate.CompilerVersion)
            .ToList();
    }

    private static IEnumerable<Installation> SplitAndMergeInstallations(
        IEnumerable<Installation> rootGroup)
    {
        Installation[] candidates = rootGroup.ToArray();
        IGrouping<(Version Version, string? TargetTriple), Installation>[] knownGroups = candidates
            .Where(candidate => candidate.CompilerVersion is not null || candidate.ProductVersion is not null)
            .GroupBy(candidate => (
                candidate.CompilerVersion ?? candidate.ProductVersion!,
                candidate.DefaultTargetTriple))
            .ToArray();
        if (knownGroups.Length <= 1)
        {
            return [MergeInstallations(candidates)];
        }

        var merged = knownGroups
            .Select(group => MergeInstallations(group))
            .ToList();
        Installation[] unknownCandidates = candidates
            .Where(candidate => candidate.CompilerVersion is null && candidate.ProductVersion is null)
            .ToArray();
        if (unknownCandidates.Length > 0)
        {
            merged.Add(MergeInstallations(unknownCandidates));
        }

        return merged;
    }

    private static Installation MergeInstallations(
        IEnumerable<Installation> candidates)
    {
        Installation[] group = candidates.ToArray();
        Installation preferred = group
            .OrderBy(candidate => Resolver.GetSourcePriority(candidate.Sources.First()))
            .First();
        Version? productVersion = preferred.ProductVersion
            ?? group.Select(candidate => candidate.ProductVersion).FirstOrDefault(version => version is not null);
        Version? compilerVersion = preferred.CompilerVersion
            ?? group.Select(candidate => candidate.CompilerVersion).FirstOrDefault(version => version is not null);
        string? defaultTargetTriple = preferred.DefaultTargetTriple
            ?? group.Select(candidate => candidate.DefaultTargetTriple)
                .FirstOrDefault(triple => !string.IsNullOrWhiteSpace(triple));
        return new Installation(
            preferred.Kind,
            preferred.CompilerFamily,
            preferred.RootPath,
            preferred.HostOS,
            preferred.HostArchitecture,
            productVersion,
            compilerVersion,
            preferred.Channel,
            group.SelectMany(candidate => candidate.Sources)
                .Distinct()
                .OrderBy(Resolver.GetSourcePriority),
            group.SelectMany(candidate => candidate.TargetPlatforms).Distinct(),
            group.SelectMany(candidate => candidate.TargetArchitectures).Distinct(),
            group.SelectMany(candidate => candidate.Components).Distinct(),
            defaultTargetTriple,
            group.SelectMany(candidate => candidate.Diagnostics));
    }

    private static List<SdkInstallation> DeduplicateSdks(
        IEnumerable<SdkInstallation> candidates)
    {
        StringComparer pathComparer = ProviderUtilities.GetPathComparer();
        return candidates
            .GroupBy(
                candidate => (candidate.Kind, candidate.TargetPlatform, candidate.RootPath, candidate.Version),
                new SdkPathKeyComparer(pathComparer))
            .Select(MergeSdks)
            .OrderBy(candidate => Resolver.GetSourcePriority(candidate.Sources.First()))
            .ThenBy(candidate => candidate.Kind)
            .ThenBy(candidate => candidate.TargetPlatform)
            .ThenByDescending(candidate => candidate.Version)
            .ToList();
    }

    private static SdkInstallation MergeSdks(
        IGrouping<
            (Kind Kind, TargetPlatform TargetPlatform, string RootPath, Version? Version),
            SdkInstallation> group)
    {
        SdkInstallation preferred = group
            .OrderBy(candidate => Resolver.GetSourcePriority(candidate.Sources.First()))
            .First();
        return new SdkInstallation(
            preferred.Kind,
            preferred.TargetPlatform,
            preferred.RootPath,
            preferred.SysrootPath,
            preferred.Version,
            group.SelectMany(candidate => candidate.Sources)
                .Distinct()
                .OrderBy(Resolver.GetSourcePriority),
            group.SelectMany(candidate => candidate.TargetArchitectures).Distinct(),
            group.SelectMany(candidate => candidate.SupportedApiLevels),
            group.SelectMany(candidate => candidate.Diagnostics));
    }

    private static void RemoveUnpairedSdks(
        List<SdkInstallation> sdks,
        IReadOnlyCollection<Installation> installations)
    {
        StringComparer pathComparer = ProviderUtilities.GetPathComparer();
        sdks.RemoveAll(sdk =>
            sdk.Kind != Kind.WindowsSdk
            && !installations.Any(installation =>
                installation.Kind == sdk.Kind
                && pathComparer.Equals(installation.RootPath, sdk.RootPath)));
    }

    private static void ValidateExplicitPaths(
        IReadOnlyCollection<PathHint>? paths,
        IReadOnlyCollection<Installation> installations,
        IReadOnlyCollection<SdkInstallation> sdks,
        IReadOnlyCollection<Diagnostic> diagnostics)
    {
        if (paths is null)
        {
            return;
        }

        foreach (PathHint hint in paths)
        {
            bool foundInstallation = installations.Any(installation =>
                installation.Kind == hint.Kind
                && (ContainsPath(hint.Path, installation.RootPath)
                    || installation.Components.Any(component => ContainsPath(hint.Path, component.Path))));
            bool foundSdk = sdks.Any(sdk =>
                sdk.Kind == hint.Kind
                && (ContainsPath(hint.Path, sdk.RootPath) || ContainsPath(hint.Path, sdk.SysrootPath)));
            if (!foundInstallation && !foundSdk)
            {
                throw new DiscoveryException(
                    $"The explicit {hint.Kind} path '{hint.Path}' did not resolve to a valid installation.",
                    diagnostics);
            }
        }
    }

    private static bool ContainsPath(string hintPath, string candidatePath)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string hint = ProviderUtilities.NormalizePath(hintPath);
        string candidate = ProviderUtilities.NormalizePath(candidatePath);
        if (string.Equals(hint, candidate, comparison))
        {
            return true;
        }

        string hintPrefix = Path.EndsInDirectorySeparator(hint)
            ? hint
            : hint + Path.DirectorySeparatorChar;
        string candidatePrefix = Path.EndsInDirectorySeparator(candidate)
            ? candidate
            : candidate + Path.DirectorySeparatorChar;
        return candidate.StartsWith(hintPrefix, comparison)
            || hint.StartsWith(candidatePrefix, comparison);
    }

    private static List<Profile> BuildProfiles(
        IReadOnlyCollection<Installation> installations,
        IReadOnlyCollection<SdkInstallation> sdks)
    {
        var profiles = new List<Profile>();
        foreach (Installation installation in installations)
        {
            SdkInstallation[] compatibleSdks = sdks
                .Where(sdk => IsCompatible(installation, sdk))
                .ToArray();
            if (RequiresSdk(installation))
            {
                foreach (SdkInstallation sdk in compatibleSdks)
                {
                    AddProfiles(profiles, installation, sdk);
                }
            }
            else
            {
                AddProfiles(profiles, installation, sdk: null);
            }
        }

        return profiles
            .GroupBy(profile => new ProfileKey(
                profile.Installation.Kind,
                profile.Installation.RootPath,
                profile.Installation.ProductVersion,
                profile.Installation.CompilerVersion,
                profile.Sdk?.RootPath,
                profile.Sdk?.Version,
                profile.TargetPlatform,
                profile.TargetArchitecture,
                profile.TargetTriple),
                new ProfileKeyComparer(ProviderUtilities.GetPathComparer()))
            .Select(group => group.First())
            .ToList();
    }

    private static void AddProfiles(
        ICollection<Profile> profiles,
        Installation installation,
        SdkInstallation? sdk)
    {
        IEnumerable<TargetPlatform> platforms = sdk is null
            ? installation.TargetPlatforms
            : [sdk.TargetPlatform];
        IEnumerable<TargetArchitecture> architectures = sdk is null
            ? installation.TargetArchitectures
            : installation.TargetArchitectures.Intersect(sdk.TargetArchitectures);

        foreach (TargetPlatform platform in platforms)
        {
            foreach (TargetArchitecture architecture in architectures)
            {
                profiles.Add(new Profile(
                    installation,
                    sdk,
                    platform,
                    architecture,
                    GetTargetTriple(platform, architecture, installation.DefaultTargetTriple)));
            }
        }
    }

    private static bool RequiresSdk(Installation installation) =>
        installation.Kind is
            Kind.VisualStudio
            or Kind.Xcode
            or Kind.AndroidNdk
            or Kind.Emscripten
            or Kind.WasiSdk
        || installation.Kind == Kind.Llvm
            && installation.TargetPlatforms.Any(platform => platform is
                TargetPlatform.Windows or TargetPlatform.MacOS);

    private static bool IsCompatible(Installation installation, SdkInstallation sdk)
    {
        if (!installation.TargetPlatforms.Contains(sdk.TargetPlatform))
        {
            return false;
        }

        bool hasSameRoot = ProviderUtilities.GetPathComparer().Equals(
            installation.RootPath,
            sdk.RootPath);
        return installation.Kind switch
        {
            Kind.VisualStudio => sdk.Kind == Kind.WindowsSdk,
            Kind.Llvm when installation.TargetPlatforms.Contains(TargetPlatform.Windows) =>
                sdk.Kind == Kind.WindowsSdk,
            Kind.Llvm when installation.TargetPlatforms.Contains(TargetPlatform.MacOS) =>
                sdk.Kind == Kind.Xcode && sdk.TargetPlatform == TargetPlatform.MacOS,
            Kind.Xcode => sdk.Kind == Kind.Xcode && hasSameRoot,
            Kind.AndroidNdk => sdk.Kind == Kind.AndroidNdk && hasSameRoot,
            Kind.Emscripten => sdk.Kind == Kind.Emscripten && hasSameRoot,
            Kind.WasiSdk => sdk.Kind == Kind.WasiSdk && hasSameRoot,
            _ => false,
        };
    }

    private static string GetTargetTriple(
        TargetPlatform platform,
        TargetArchitecture architecture,
        string? reportedTriple)
    {
        if (!string.IsNullOrWhiteSpace(reportedTriple))
        {
            return reportedTriple;
        }

        if (platform == TargetPlatform.Android
            && architecture == TargetArchitecture.ARM)
        {
            return "armv7a-linux-androideabi";
        }

        bool isApplePlatform = platform is
            TargetPlatform.MacOS
            or TargetPlatform.IOS
            or TargetPlatform.IOSSimulator
            or TargetPlatform.TvOS
            or TargetPlatform.TvOSSimulator
            or TargetPlatform.WatchOS
            or TargetPlatform.WatchOSSimulator
            or TargetPlatform.VisionOS
            or TargetPlatform.VisionOSSimulator;
        string architectureName = architecture switch
        {
            TargetArchitecture.X86 => "i686",
            TargetArchitecture.X64 => "x86_64",
            TargetArchitecture.ARM => "armv7",
            TargetArchitecture.ARM64 when isApplePlatform => "arm64",
            TargetArchitecture.ARM64 => "aarch64",
            TargetArchitecture.Wasm32 => "wasm32",
            _ => "unknown",
        };
        string platformName = platform switch
        {
            TargetPlatform.Windows => "pc-windows-msvc",
            TargetPlatform.Linux => "unknown-linux-gnu",
            TargetPlatform.MacOS => "apple-darwin",
            TargetPlatform.IOS => "apple-ios",
            TargetPlatform.IOSSimulator => "apple-ios-simulator",
            TargetPlatform.TvOS => "apple-tvos",
            TargetPlatform.TvOSSimulator => "apple-tvos-simulator",
            TargetPlatform.WatchOS => "apple-watchos",
            TargetPlatform.WatchOSSimulator => "apple-watchos-simulator",
            TargetPlatform.VisionOS => "apple-xros",
            TargetPlatform.VisionOSSimulator => "apple-xros-simulator",
            TargetPlatform.Android => "linux-android",
            TargetPlatform.Emscripten => "unknown-emscripten",
            TargetPlatform.Wasi => "wasi",
            _ => "unknown",
        };
        return $"{architectureName}-{platformName}";
    }

    private static string CreateCacheKey(
        DiscoveryOptions options,
        IReadOnlyDictionary<string, string?> environment)
    {
        var builder = new StringBuilder();
        builder.Append(options.IncludePreview ? '1' : '0');
        AppendCacheKeyValue(builder, options.ProbeTimeout.Ticks.ToString(CultureInfo.InvariantCulture));
        IEnumerable<Kind> kinds = options.Kinds is null
            ? Enum.GetValues<Kind>()
            : options.Kinds.Order();
        foreach (Kind kind in kinds)
        {
            AppendCacheKeyValue(builder, ((int)kind).ToString(CultureInfo.InvariantCulture));
        }

        IEnumerable<PathHint> paths = options.ExplicitPaths is null
            ? []
            : options.ExplicitPaths.OrderBy(path => path.Kind)
                .ThenBy(path => path.Path, ProviderUtilities.GetPathComparer());
        foreach (PathHint path in paths)
        {
            AppendCacheKeyValue(builder, ((int)path.Kind).ToString(CultureInfo.InvariantCulture));
            AppendCacheKeyValue(builder, path.Path);
        }

        foreach ((string name, string? value) in environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            AppendCacheKeyValue(builder, name);
            AppendCacheKeyValue(builder, value);
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(digest);
    }

    private static void AppendCacheKeyValue(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }

        builder.Append(value.Length).Append(':').Append(value);
    }

    private sealed class InstallationPathKeyComparer(StringComparer pathComparer)
        : IEqualityComparer<(Kind Kind, string RootPath)>
    {
        public bool Equals(
            (Kind Kind, string RootPath) left,
            (Kind Kind, string RootPath) right) =>
            left.Kind == right.Kind && pathComparer.Equals(left.RootPath, right.RootPath);

        public int GetHashCode((Kind Kind, string RootPath) value) =>
            HashCode.Combine(value.Kind, pathComparer.GetHashCode(value.RootPath));
    }

    private sealed class SdkPathKeyComparer(StringComparer pathComparer)
        : IEqualityComparer<(Kind Kind, TargetPlatform TargetPlatform, string RootPath, Version? Version)>
    {
        public bool Equals(
            (Kind Kind, TargetPlatform TargetPlatform, string RootPath, Version? Version) left,
            (Kind Kind, TargetPlatform TargetPlatform, string RootPath, Version? Version) right) =>
            left.Kind == right.Kind
            && left.TargetPlatform == right.TargetPlatform
            && left.Version == right.Version
            && pathComparer.Equals(left.RootPath, right.RootPath);

        public int GetHashCode(
            (Kind Kind, TargetPlatform TargetPlatform, string RootPath, Version? Version) value) =>
            HashCode.Combine(
                value.Kind,
                value.TargetPlatform,
                value.Version,
                pathComparer.GetHashCode(value.RootPath));
    }

    private readonly record struct ProfileKey(
        Kind Kind,
        string RootPath,
        Version? ProductVersion,
        Version? CompilerVersion,
        string? SdkRoot,
        Version? SdkVersion,
        TargetPlatform TargetPlatform,
        TargetArchitecture TargetArchitecture,
        string TargetTriple);

    private sealed class ProfileKeyComparer(StringComparer pathComparer) : IEqualityComparer<ProfileKey>
    {
        public bool Equals(ProfileKey left, ProfileKey right) =>
            left.Kind == right.Kind
            && left.ProductVersion == right.ProductVersion
            && left.CompilerVersion == right.CompilerVersion
            && left.SdkVersion == right.SdkVersion
            && left.TargetPlatform == right.TargetPlatform
            && left.TargetArchitecture == right.TargetArchitecture
            && string.Equals(left.TargetTriple, right.TargetTriple, StringComparison.Ordinal)
            && pathComparer.Equals(left.RootPath, right.RootPath)
            && pathComparer.Equals(left.SdkRoot, right.SdkRoot);

        public int GetHashCode(ProfileKey value)
        {
            var hash = new HashCode();
            hash.Add(value.Kind);
            hash.Add(value.RootPath, pathComparer);
            hash.Add(value.ProductVersion);
            hash.Add(value.CompilerVersion);
            hash.Add(value.SdkRoot, pathComparer);
            hash.Add(value.SdkVersion);
            hash.Add(value.TargetPlatform);
            hash.Add(value.TargetArchitecture);
            hash.Add(value.TargetTriple, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}
