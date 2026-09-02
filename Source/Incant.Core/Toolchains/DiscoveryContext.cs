using Incant.Base;

namespace Incant.Core.Toolchains;

/// <summary>Provides immutable inputs shared by all providers in one discovery operation.</summary>
public sealed class DiscoveryContext
{
    internal DiscoveryContext(
        DiscoveryOptions options,
        IReadOnlyDictionary<string, string?> environment)
    {
        Options = options;
        Environment = environment;
        HostOS = Platform.OS;
        HostArchitecture = ProviderUtilities.GetHostArchitecture();
    }

    /// <summary>Gets the caller's discovery options.</summary>
    public DiscoveryOptions Options { get; }

    /// <summary>Gets the captured environment.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; }

    /// <summary>Gets the current host operating system.</summary>
    public PlatformOS HostOS { get; }

    /// <summary>Gets the current host architecture.</summary>
    public TargetArchitecture HostArchitecture { get; }

    /// <summary>Gets explicit paths associated with one family.</summary>
    /// <param name="kind">The requested family.</param>
    /// <returns>The matching explicit path hints.</returns>
    public IEnumerable<PathHint> GetExplicitPaths(Kind kind) =>
        Options.ExplicitPaths?.Where(path => path.Kind == kind) ?? [];

    /// <summary>Reads a captured environment variable.</summary>
    /// <param name="name">The environment-variable name.</param>
    /// <returns>The captured value, or null when the variable is absent or explicitly removed.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public string? GetEnvironmentVariable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Environment.TryGetValue(name, out string? value) ? value : null;
    }
}
