namespace Incant.Core.Toolchains;

/// <summary>Describes the toolchain profile requested by a caller.</summary>
public sealed class Selection
{
    /// <summary>Gets the required compiler toolchain family.</summary>
    public Kind? Kind { get; init; }

    /// <summary>Gets the required SDK family.</summary>
    public Kind? SdkKind { get; init; }

    /// <summary>Gets the required target platform.</summary>
    public TargetPlatform? TargetPlatform { get; init; }

    /// <summary>Gets the required target architecture.</summary>
    public TargetArchitecture? TargetArchitecture { get; init; }

    /// <summary>Gets the enclosing product version constraint.</summary>
    public VersionConstraint? ProductVersion { get; init; }

    /// <summary>Gets the compiler version constraint.</summary>
    public VersionConstraint? CompilerVersion { get; init; }

    /// <summary>Gets the SDK version constraint.</summary>
    public VersionConstraint? SdkVersion { get; init; }

    /// <summary>Gets a value indicating whether preview profiles are accepted.</summary>
    public bool IncludePreview { get; init; }
}
