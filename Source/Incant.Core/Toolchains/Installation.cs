using System.Collections.ObjectModel;
using Incant.Base;

namespace Incant.Core.Toolchains;

/// <summary>Describes one validated compiler toolchain installation.</summary>
public sealed class Installation
{
    /// <summary>Initializes an immutable toolchain installation.</summary>
    /// <param name="kind">The toolchain family.</param>
    /// <param name="compilerFamily">The compiler implementation.</param>
    /// <param name="rootPath">The installation root.</param>
    /// <param name="hostOS">The host operating system on which the installation was validated.</param>
    /// <param name="hostArchitecture">The host architecture on which the installation was validated.</param>
    /// <param name="productVersion">The enclosing product version.</param>
    /// <param name="compilerVersion">The compiler version.</param>
    /// <param name="channel">The release channel.</param>
    /// <param name="sources">The discovery sources in priority order.</param>
    /// <param name="targetPlatforms">The supported target platforms.</param>
    /// <param name="targetArchitectures">The supported target architectures.</param>
    /// <param name="components">The validated components.</param>
    /// <param name="defaultTargetTriple">The compiler-reported default target triple.</param>
    /// <param name="diagnostics">Installation-specific diagnostics.</param>
    /// <exception cref="ArgumentException">A required path is empty or no source is supplied.</exception>
    /// <exception cref="ArgumentNullException">A required collection is null.</exception>
    public Installation(
        Kind kind,
        CompilerFamily compilerFamily,
        string rootPath,
        PlatformOS hostOS,
        TargetArchitecture hostArchitecture,
        Version? productVersion,
        Version? compilerVersion,
        Channel channel,
        IEnumerable<Source> sources,
        IEnumerable<TargetPlatform> targetPlatforms,
        IEnumerable<TargetArchitecture> targetArchitectures,
        IEnumerable<Component> components,
        string? defaultTargetTriple = null,
        IEnumerable<Diagnostic>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(targetPlatforms);
        ArgumentNullException.ThrowIfNull(targetArchitectures);
        ArgumentNullException.ThrowIfNull(components);

        Kind = kind;
        CompilerFamily = compilerFamily;
        RootPath = ProviderUtilities.NormalizePath(rootPath);
        HostOS = hostOS;
        HostArchitecture = hostArchitecture;
        ProductVersion = productVersion;
        CompilerVersion = compilerVersion;
        Channel = channel;
        Sources = Array.AsReadOnly(sources
            .Distinct()
            .OrderBy(Resolver.GetSourcePriority)
            .ToArray());
        if (Sources.Count == 0)
        {
            throw new ArgumentException("At least one discovery source is required.", nameof(sources));
        }

        TargetPlatforms = FreezeDistinct(targetPlatforms);
        TargetArchitectures = FreezeDistinct(targetArchitectures);
        Components = Array.AsReadOnly(components.ToArray());
        DefaultTargetTriple = defaultTargetTriple;
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
    }

    /// <summary>Gets the toolchain family.</summary>
    public Kind Kind { get; }

    /// <summary>Gets the compiler implementation.</summary>
    public CompilerFamily CompilerFamily { get; }

    /// <summary>Gets the absolute installation root.</summary>
    public string RootPath { get; }

    /// <summary>Gets the host operating system on which this installation was validated.</summary>
    public PlatformOS HostOS { get; }

    /// <summary>Gets the host architecture on which this installation was validated.</summary>
    public TargetArchitecture HostArchitecture { get; }

    /// <summary>Gets the enclosing product version, when available.</summary>
    public Version? ProductVersion { get; }

    /// <summary>Gets the compiler version, when available.</summary>
    public Version? CompilerVersion { get; }

    /// <summary>Gets the release channel.</summary>
    public Channel Channel { get; }

    /// <summary>Gets the ordered discovery sources.</summary>
    public IReadOnlyList<Source> Sources { get; }

    /// <summary>Gets the supported target platforms.</summary>
    public IReadOnlyList<TargetPlatform> TargetPlatforms { get; }

    /// <summary>Gets the supported target architectures.</summary>
    public IReadOnlyList<TargetArchitecture> TargetArchitectures { get; }

    /// <summary>Gets the validated components.</summary>
    public IReadOnlyList<Component> Components { get; }

    /// <summary>Gets the compiler-reported default target triple.</summary>
    public string? DefaultTargetTriple { get; }

    /// <summary>Gets installation-specific diagnostics.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    private static ReadOnlyCollection<T> FreezeDistinct<T>(IEnumerable<T> values)
        where T : struct, Enum => Array.AsReadOnly(values.Distinct().ToArray());
}
