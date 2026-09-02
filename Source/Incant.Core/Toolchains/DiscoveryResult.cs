namespace Incant.Core.Toolchains;

/// <summary>Contains candidates and diagnostics returned by one provider.</summary>
public sealed class DiscoveryResult
{
    /// <summary>Initializes a provider result.</summary>
    /// <param name="installations">Validated compiler installations.</param>
    /// <param name="sdks">Validated platform SDKs.</param>
    /// <param name="diagnostics">Discovery diagnostics.</param>
    public DiscoveryResult(
        IEnumerable<Installation>? installations = null,
        IEnumerable<SdkInstallation>? sdks = null,
        IEnumerable<Diagnostic>? diagnostics = null)
    {
        Installations = Array.AsReadOnly((installations ?? []).ToArray());
        Sdks = Array.AsReadOnly((sdks ?? []).ToArray());
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
    }

    /// <summary>Gets validated compiler installations.</summary>
    public IReadOnlyList<Installation> Installations { get; }

    /// <summary>Gets validated platform SDKs.</summary>
    public IReadOnlyList<SdkInstallation> Sdks { get; }

    /// <summary>Gets provider diagnostics.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}
