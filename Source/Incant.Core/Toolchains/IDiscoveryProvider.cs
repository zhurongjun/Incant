namespace Incant.Core.Toolchains;

/// <summary>Discovers one or more related toolchain or SDK families.</summary>
public interface IDiscoveryProvider
{
    /// <summary>Gets the provider's diagnostic name.</summary>
    string Name { get; }

    /// <summary>Gets the families handled by this provider.</summary>
    IReadOnlyCollection<Kind> Kinds { get; }

    /// <summary>Discovers candidates without modifying global machine state.</summary>
    /// <param name="context">The immutable discovery context.</param>
    /// <param name="cancellationToken">A token that cancels discovery.</param>
    /// <returns>The discovered candidates and diagnostics.</returns>
    ValueTask<DiscoveryResult> DiscoverAsync(
        DiscoveryContext context,
        CancellationToken cancellationToken = default);
}
