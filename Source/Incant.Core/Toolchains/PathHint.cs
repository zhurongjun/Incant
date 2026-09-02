namespace Incant.Core.Toolchains;

/// <summary>Associates an explicit installation path with a toolchain family.</summary>
public sealed record PathHint
{
    /// <summary>Initializes an explicit path hint.</summary>
    /// <param name="kind">The expected family.</param>
    /// <param name="path">The installation, SDK, or component path.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public PathHint(Kind kind, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Kind = kind;
        Path = ProviderUtilities.NormalizePath(path);
    }

    /// <summary>Gets the expected toolchain family.</summary>
    public Kind Kind { get; }

    /// <summary>Gets the absolute candidate path.</summary>
    public string Path { get; }
}
