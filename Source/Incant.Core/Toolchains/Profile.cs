namespace Incant.Core.Toolchains;

/// <summary>Describes a compatible toolchain, SDK, platform, and architecture selection.</summary>
public sealed record Profile
{
    /// <summary>Initializes an immutable toolchain profile.</summary>
    /// <param name="installation">The compiler installation.</param>
    /// <param name="sdk">The paired SDK, or null when no separate SDK is required.</param>
    /// <param name="targetPlatform">The target platform.</param>
    /// <param name="targetArchitecture">The target architecture.</param>
    /// <param name="targetTriple">The compiler target triple.</param>
    /// <exception cref="ArgumentNullException"><paramref name="installation"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="targetTriple"/> is empty.</exception>
    public Profile(
        Installation installation,
        SdkInstallation? sdk,
        TargetPlatform targetPlatform,
        TargetArchitecture targetArchitecture,
        string targetTriple)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTriple);

        Installation = installation;
        Sdk = sdk;
        TargetPlatform = targetPlatform;
        TargetArchitecture = targetArchitecture;
        TargetTriple = targetTriple;
    }

    /// <summary>Gets the compiler installation.</summary>
    public Installation Installation { get; }

    /// <summary>Gets the paired SDK, or null when no separate SDK is required.</summary>
    public SdkInstallation? Sdk { get; }

    /// <summary>Gets the target platform.</summary>
    public TargetPlatform TargetPlatform { get; }

    /// <summary>Gets the target architecture.</summary>
    public TargetArchitecture TargetArchitecture { get; }

    /// <summary>Gets the target triple.</summary>
    public string TargetTriple { get; }
}
