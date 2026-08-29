namespace Incant.Base.Log;

/// <summary>Identifies the severity of a log event.</summary>
public enum LogLevel
{
    /// <summary>Fine-grained execution details.</summary>
    Trace,

    /// <summary>Diagnostic information useful while investigating a build.</summary>
    Debug,

    /// <summary>Normal build progress and noteworthy events.</summary>
    Info,

    /// <summary>A recoverable condition that may require attention.</summary>
    Warning,

    /// <summary>An operation failed.</summary>
    Error,

    /// <summary>The build process cannot continue normally.</summary>
    Fatal,

    /// <summary>Disables log events.</summary>
    None,
}
