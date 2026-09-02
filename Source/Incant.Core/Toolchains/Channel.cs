namespace Incant.Core.Toolchains;

/// <summary>Identifies the release channel of an installation.</summary>
public enum Channel
{
    /// <summary>The release channel could not be determined.</summary>
    Unknown,

    /// <summary>A stable release.</summary>
    Stable,

    /// <summary>A preview or release-candidate build.</summary>
    Preview,

    /// <summary>An experimental development build.</summary>
    Experimental,
}
