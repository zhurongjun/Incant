namespace Incant.Core.Toolchains;

/// <summary>Describes one executable or resource belonging to a toolchain.</summary>
public sealed record Component
{
    /// <summary>Initializes a toolchain component.</summary>
    /// <param name="kind">The component role.</param>
    /// <param name="path">The component's file or directory path.</param>
    /// <param name="hostArchitecture">The host architecture required to run the component.</param>
    /// <param name="targetArchitecture">The architecture produced by the component.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public Component(
        ComponentKind kind,
        string path,
        TargetArchitecture hostArchitecture = TargetArchitecture.Unknown,
        TargetArchitecture targetArchitecture = TargetArchitecture.Unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Kind = kind;
        Path = ProviderUtilities.NormalizePath(path);
        HostArchitecture = hostArchitecture;
        TargetArchitecture = targetArchitecture;
    }

    /// <summary>Gets the component kind.</summary>
    public ComponentKind Kind { get; }

    /// <summary>Gets the absolute component path.</summary>
    public string Path { get; }

    /// <summary>Gets the required host architecture, or Unknown when unrestricted.</summary>
    public TargetArchitecture HostArchitecture { get; }

    /// <summary>Gets the produced target architecture, or Unknown when unrestricted.</summary>
    public TargetArchitecture TargetArchitecture { get; }
}
