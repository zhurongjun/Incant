namespace Incant.Core.Toolchains;

/// <summary>Provides an immutable snapshot of one discovery operation.</summary>
public sealed class Catalog
{
    /// <summary>Initializes a discovery catalog.</summary>
    /// <param name="installations">The validated compiler installations.</param>
    /// <param name="sdks">The validated platform SDKs.</param>
    /// <param name="profiles">The compatible profiles.</param>
    /// <param name="diagnostics">Discovery diagnostics.</param>
    /// <exception cref="ArgumentNullException">A required collection is null.</exception>
    public Catalog(
        IEnumerable<Installation> installations,
        IEnumerable<SdkInstallation> sdks,
        IEnumerable<Profile> profiles,
        IEnumerable<Diagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(installations);
        ArgumentNullException.ThrowIfNull(sdks);
        ArgumentNullException.ThrowIfNull(profiles);

        Installations = Array.AsReadOnly(installations.ToArray());
        Sdks = Array.AsReadOnly(sdks.ToArray());
        Profiles = Array.AsReadOnly(profiles.ToArray());
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
    }

    /// <summary>Gets all validated compiler installations.</summary>
    public IReadOnlyList<Installation> Installations { get; }

    /// <summary>Gets all validated platform SDKs.</summary>
    public IReadOnlyList<SdkInstallation> Sdks { get; }

    /// <summary>Gets all compatible toolchain profiles.</summary>
    public IReadOnlyList<Profile> Profiles { get; }

    /// <summary>Gets all discovery diagnostics.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}
