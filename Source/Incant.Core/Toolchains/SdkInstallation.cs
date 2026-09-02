namespace Incant.Core.Toolchains;

/// <summary>Describes one validated target platform SDK.</summary>
public sealed class SdkInstallation
{
    /// <summary>Initializes an immutable SDK installation.</summary>
    /// <param name="kind">The provider family that owns the SDK.</param>
    /// <param name="targetPlatform">The target platform.</param>
    /// <param name="rootPath">The SDK installation root.</param>
    /// <param name="sysrootPath">The target sysroot.</param>
    /// <param name="version">The SDK version.</param>
    /// <param name="sources">The discovery sources in priority order.</param>
    /// <param name="targetArchitectures">The supported target architectures.</param>
    /// <param name="supportedApiLevels">The supported numeric platform API levels.</param>
    /// <param name="diagnostics">SDK-specific diagnostics.</param>
    /// <exception cref="ArgumentException">A required path is empty or no source is supplied.</exception>
    /// <exception cref="ArgumentNullException">A required collection is null.</exception>
    public SdkInstallation(
        Kind kind,
        TargetPlatform targetPlatform,
        string rootPath,
        string sysrootPath,
        Version? version,
        IEnumerable<Source> sources,
        IEnumerable<TargetArchitecture> targetArchitectures,
        IEnumerable<int>? supportedApiLevels = null,
        IEnumerable<Diagnostic>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sysrootPath);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(targetArchitectures);

        Kind = kind;
        TargetPlatform = targetPlatform;
        RootPath = ProviderUtilities.NormalizePath(rootPath);
        SysrootPath = ProviderUtilities.NormalizePath(sysrootPath);
        Version = version;
        Sources = Array.AsReadOnly(sources
            .Distinct()
            .OrderBy(Resolver.GetSourcePriority)
            .ToArray());
        if (Sources.Count == 0)
        {
            throw new ArgumentException("At least one discovery source is required.", nameof(sources));
        }

        TargetArchitectures = Array.AsReadOnly(targetArchitectures.Distinct().ToArray());
        SupportedApiLevels = Array.AsReadOnly((supportedApiLevels ?? [])
            .Distinct()
            .Order()
            .ToArray());
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
    }

    /// <summary>Gets the provider family that owns the SDK.</summary>
    public Kind Kind { get; }

    /// <summary>Gets the target platform.</summary>
    public TargetPlatform TargetPlatform { get; }

    /// <summary>Gets the absolute SDK root.</summary>
    public string RootPath { get; }

    /// <summary>Gets the absolute target sysroot.</summary>
    public string SysrootPath { get; }

    /// <summary>Gets the SDK version, when available.</summary>
    public Version? Version { get; }

    /// <summary>Gets the ordered discovery sources.</summary>
    public IReadOnlyList<Source> Sources { get; }

    /// <summary>Gets the supported target architectures.</summary>
    public IReadOnlyList<TargetArchitecture> TargetArchitectures { get; }

    /// <summary>Gets supported numeric platform API levels, or an empty list when not applicable.</summary>
    public IReadOnlyList<int> SupportedApiLevels { get; }

    /// <summary>Gets SDK-specific diagnostics.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}
