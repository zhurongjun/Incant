namespace Incant.CXLegacy.Arguments;

/// <summary>Severity of a configuration interpretation diagnostic.</summary>
public enum ArgumentDiagnosticSeverity
{
    /// <summary>An actionable warning; generation can continue.</summary>
    Warning,
    /// <summary>An error; no executable argument list is returned.</summary>
    Error,
}

/// <summary>A configuration diagnostic associated with its original contributions.</summary>
public sealed class ArgumentDiagnostic
{
    /// <summary>Creates an immutable diagnostic.</summary>
    public ArgumentDiagnostic(ArgumentDiagnosticSeverity severity, ArgumentField field, string message,
        IEnumerable<ArgumentOrigin>? origins = null)
    {
        if (!Enum.IsDefined(field))
        {
            throw new ArgumentOutOfRangeException(nameof(field));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        Severity = severity;
        Field = field;
        Message = message;
        ArgumentOrigin[] sources = (origins ?? []).ToArray();
        if (sources.Any(origin => origin is null))
        {
            throw new ArgumentException("Origins cannot contain null.", nameof(origins));
        }

        Origins = Array.AsReadOnly(sources);
    }

    /// <summary>Gets the severity.</summary>
    public ArgumentDiagnosticSeverity Severity { get; }

    /// <summary>Gets the configuration identifier.</summary>
    public ArgumentField Field { get; }

    /// <summary>Gets the reason.</summary>
    public string Message { get; }

    /// <summary>Gets the contributing sources.</summary>
    public IReadOnlyList<ArgumentOrigin> Origins { get; }
}

/// <summary>Immutable logical arguments and diagnostics. Errors suppress all arguments.</summary>
public sealed class ArgumentGenerationResult
{
    /// <summary>Snapshots unescaped, individual process arguments and diagnostics.</summary>
    public ArgumentGenerationResult(IEnumerable<string> arguments, IEnumerable<ArgumentDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentDiagnostic[] issues = (diagnostics ?? []).ToArray();
        if (issues.Any(diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Diagnostics cannot contain null.", nameof(diagnostics));
        }

        Diagnostics = Array.AsReadOnly(issues);
        string[] values = arguments.ToArray();
        if (values.Any(value => value is null || value.Contains('\0')))
        {
            throw new ArgumentException("Arguments cannot be null or contain NUL.", nameof(arguments));
        }

        Arguments = Array.AsReadOnly(Success ? values : []);
    }

    /// <summary>Gets whether generation has no errors.</summary>
    public bool Success => Diagnostics.All(item => item.Severity != ArgumentDiagnosticSeverity.Error);

    /// <summary>Gets ordered, unescaped arguments, or an empty list on failure.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets generation diagnostics.</summary>
    public IReadOnlyList<ArgumentDiagnostic> Diagnostics { get; }
}
