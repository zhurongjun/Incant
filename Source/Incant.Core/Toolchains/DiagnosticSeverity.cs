namespace Incant.Core.Toolchains;

/// <summary>Identifies the severity of a discovery diagnostic.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational context.</summary>
    Info,

    /// <summary>A recoverable candidate problem.</summary>
    Warning,

    /// <summary>A discovery failure.</summary>
    Error,
}
