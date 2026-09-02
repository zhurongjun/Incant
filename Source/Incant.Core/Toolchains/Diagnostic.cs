namespace Incant.Core.Toolchains;

/// <summary>Describes one observation made while discovering toolchains.</summary>
public sealed record Diagnostic
{
    /// <summary>Initializes a diagnostic.</summary>
    /// <param name="severity">The diagnostic severity.</param>
    /// <param name="code">The stable diagnostic code.</param>
    /// <param name="provider">The provider name.</param>
    /// <param name="message">The human-readable explanation.</param>
    /// <param name="path">The related path, when available.</param>
    /// <exception cref="ArgumentException">A required string is empty.</exception>
    public Diagnostic(
        DiagnosticSeverity severity,
        string code,
        string provider,
        string message,
        string? path = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Severity = severity;
        Code = code;
        Provider = provider;
        Message = message;
        Path = path;
    }

    /// <summary>Gets the diagnostic severity.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Gets the stable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Gets the provider that produced the diagnostic.</summary>
    public string Provider { get; }

    /// <summary>Gets the human-readable diagnostic message.</summary>
    public string Message { get; }

    /// <summary>Gets the related path, when available.</summary>
    public string? Path { get; }
}
