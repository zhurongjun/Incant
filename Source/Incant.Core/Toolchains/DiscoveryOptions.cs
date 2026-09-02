namespace Incant.Core.Toolchains;

/// <summary>Configures one toolchain discovery operation.</summary>
public sealed class DiscoveryOptions
{
    /// <summary>Gets the families to discover, or null to discover every built-in family.</summary>
    public IReadOnlyCollection<Kind>? Kinds { get; init; }

    /// <summary>Gets caller-supplied installation paths that must resolve successfully.</summary>
    public IReadOnlyCollection<PathHint>? ExplicitPaths { get; init; }

    /// <summary>Gets a value indicating whether preview and experimental installations are accepted.</summary>
    public bool IncludePreview { get; init; }

    /// <summary>Gets a value indicating whether an existing per-service cache entry should be replaced.</summary>
    public bool Refresh { get; init; }

    /// <summary>Gets an optional environment snapshot used instead of the current process environment.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Gets the maximum duration of one external probe.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
