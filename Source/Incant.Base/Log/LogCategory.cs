namespace Incant.Base.Log;

/// <summary>Identifies the subsystem that produced a log event.</summary>
public readonly struct LogCategory : IEquatable<LogCategory>
{
    private const string GeneralName = "General";

    private readonly string? _name;

    /// <summary>The default category.</summary>
    public static LogCategory General { get; } = new(GeneralName);

    /// <summary>Build orchestration.</summary>
    public static LogCategory Build { get; } = new("Build");

    /// <summary>Dependency discovery and resolution.</summary>
    public static LogCategory Dependency { get; } = new("Dependency");

    /// <summary>Work scheduling.</summary>
    public static LogCategory Scheduler { get; } = new("Scheduler");

    /// <summary>Child-process execution.</summary>
    public static LogCategory Process { get; } = new("Process");

    /// <summary>File-system and stream input/output.</summary>
    public static LogCategory IO { get; } = new("IO");

    /// <summary>Build cache operations.</summary>
    public static LogCategory Cache { get; } = new("Cache");

    /// <summary>Diagnostics produced by the log system itself.</summary>
    public static LogCategory Logging { get; } = new("Logging");

    /// <summary>Initializes a named log category.</summary>
    /// <param name="name">A non-empty name that does not contain brackets or control characters.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or contains an invalid character.</exception>
    public LogCategory(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        foreach (char character in name)
        {
            if (character is '[' or ']' || char.IsControl(character))
            {
                throw new ArgumentException("A log category cannot contain brackets or control characters.", nameof(name));
            }
        }

        _name = name;
    }

    /// <summary>Gets the category name. The default struct value resolves to <c>General</c>.</summary>
    public string Name => _name ?? GeneralName;

    /// <inheritdoc />
    public bool Equals(LogCategory other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LogCategory other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <inheritdoc />
    public override string ToString() => Name;

    /// <summary>Determines whether two categories have the same name.</summary>
    public static bool operator ==(LogCategory left, LogCategory right) => left.Equals(right);

    /// <summary>Determines whether two categories have different names.</summary>
    public static bool operator !=(LogCategory left, LogCategory right) => !left.Equals(right);
}
