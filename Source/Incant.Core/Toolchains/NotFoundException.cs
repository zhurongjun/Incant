namespace Incant.Core.Toolchains;

/// <summary>Reports that no discovered profile satisfies a selection.</summary>
public sealed class NotFoundException : DiscoveryException
{
    /// <summary>Initializes a profile-selection failure.</summary>
    /// <param name="message">The failure explanation.</param>
    /// <param name="diagnostics">Diagnostics associated with the failure.</param>
    public NotFoundException(string message, IEnumerable<Diagnostic>? diagnostics = null)
        : base(message, diagnostics)
    {
    }
}
