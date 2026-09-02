namespace Incant.Core.Toolchains;

/// <summary>Selects the best matching profile from an immutable catalog.</summary>
public static class Resolver
{
    /// <summary>Returns the highest-priority profile satisfying a selection.</summary>
    /// <param name="catalog">The catalog to search.</param>
    /// <param name="selection">The required profile properties.</param>
    /// <returns>The highest-priority matching profile.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="NotFoundException">No profile satisfies the selection.</exception>
    public static Profile Resolve(Catalog catalog, Selection selection)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selection);

        Profile? profile = catalog.Profiles
            .Where(profile => Matches(profile, selection))
            .OrderBy(profile => GetSourcePriority(profile.Installation.Sources.First()))
            .ThenBy(profile => profile.Sdk is null
                ? 0
                : GetSourcePriority(profile.Sdk.Sources.First()))
            .ThenByDescending(profile => profile.Installation.ProductVersion)
            .ThenByDescending(profile => profile.Installation.CompilerVersion)
            .ThenByDescending(profile => profile.Sdk?.Version)
            .FirstOrDefault();

        return profile ?? throw new NotFoundException(
            "No discovered toolchain profile satisfies the requested selection.",
            catalog.Diagnostics);
    }

    internal static int GetSourcePriority(Source source) => source switch
    {
        Source.Explicit => 0,
        Source.Environment => 1,
        Source.Vendor => 2,
        Source.StandardPath => 3,
        Source.Path => 4,
        _ => int.MaxValue,
    };

    private static bool Matches(Profile profile, Selection selection)
    {
        if (selection.Kind is Kind requiredKind
            && profile.Installation.Kind != requiredKind)
        {
            return false;
        }

        if (selection.SdkKind is Kind sdkKind && profile.Sdk?.Kind != sdkKind)
        {
            return false;
        }

        if (selection.TargetPlatform is TargetPlatform platform
            && profile.TargetPlatform != platform)
        {
            return false;
        }

        if (selection.TargetArchitecture is TargetArchitecture architecture
            && profile.TargetArchitecture != architecture)
        {
            return false;
        }

        if (!selection.IncludePreview && profile.Installation.Channel != Channel.Stable)
        {
            return false;
        }

        return (selection.ProductVersion?.Matches(profile.Installation.ProductVersion) ?? true)
            && (selection.CompilerVersion?.Matches(profile.Installation.CompilerVersion) ?? true)
            && (selection.SdkVersion?.Matches(profile.Sdk?.Version) ?? true);
    }
}
