namespace Incant.Core.Toolchains;

/// <summary>Reports that a required discovery input could not be resolved.</summary>
public class DiscoveryException : Exception
{
    /// <summary>Initializes a discovery exception.</summary>
    /// <param name="message">The failure explanation.</param>
    /// <param name="diagnostics">Diagnostics associated with the failure.</param>
    /// <param name="innerException">The underlying exception, when available.</param>
    public DiscoveryException(
        string message,
        IEnumerable<Diagnostic>? diagnostics = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
    }

    /// <summary>Gets diagnostics associated with the failure.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}
