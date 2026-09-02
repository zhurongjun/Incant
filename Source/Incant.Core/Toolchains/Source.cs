namespace Incant.Core.Toolchains;

/// <summary>Identifies how an installation candidate was found.</summary>
public enum Source
{
    /// <summary>The caller supplied the path explicitly.</summary>
    Explicit,

    /// <summary>An active environment variable supplied the path.</summary>
    Environment,

    /// <summary>A vendor discovery mechanism supplied the path.</summary>
    Vendor,

    /// <summary>A conventional installation directory supplied the path.</summary>
    StandardPath,

    /// <summary>The executable search path supplied the path.</summary>
    Path,
}
