namespace Incant.CX;

/// <summary>Identifies the release channel of an installation.</summary>
public enum Channel
{
    /// <summary>The release channel could not be determined.</summary>
    Unknown,

    /// <summary>A stable release.</summary>
    Stable,

    /// <summary>A preview or release-candidate build.</summary>
    Preview,

    /// <summary>An experimental development build.</summary>
    Experimental,
}

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

/// <summary>Identifies how an installation candidate was found.</summary>
public enum Source
{
    /// <summary>The caller supplied the path explicitly.</summary>
    Explicit,

    /// <summary>An active environment variable supplied the path.</summary>
    Environment,

    /// <summary>A vendor discovery mechanism supplied the path.</summary>
    Vendor,

    /// <summary>A conventional installation directory supplied the path.</summary>
    StandardPath,

    /// <summary>The executable search path supplied the path.</summary>
    Path,
}

/// <summary>Identifies a supported target processor architecture.</summary>
public enum TargetArchitecture
{
    /// <summary>The architecture is unknown.</summary>
    Unknown,

    /// <summary>The 32-bit x86 architecture.</summary>
    X86,

    /// <summary>The 64-bit x86 architecture.</summary>
    X64,

    /// <summary>The 32-bit Arm architecture.</summary>
    ARM,

    /// <summary>The 64-bit Arm architecture.</summary>
    ARM64,

    /// <summary>The 32-bit WebAssembly architecture.</summary>
    Wasm32,
}

/// <summary>Identifies a supported compilation target platform.</summary>
public enum TargetPlatform
{
    /// <summary>The target platform is unknown.</summary>
    Unknown,

    /// <summary>Microsoft Windows.</summary>
    Windows,

    /// <summary>Linux.</summary>
    Linux,

    /// <summary>Apple macOS.</summary>
    MacOS,

    /// <summary>Apple iOS devices.</summary>
    IOS,

    /// <summary>The Apple iOS simulator.</summary>
    IOSSimulator,

    /// <summary>Apple tvOS devices.</summary>
    TvOS,

    /// <summary>The Apple tvOS simulator.</summary>
    TvOSSimulator,

    /// <summary>Apple watchOS devices.</summary>
    WatchOS,

    /// <summary>The Apple watchOS simulator.</summary>
    WatchOSSimulator,

    /// <summary>Apple visionOS devices.</summary>
    VisionOS,

    /// <summary>The Apple visionOS simulator.</summary>
    VisionOSSimulator,

    /// <summary>Android.</summary>
    Android,

    /// <summary>The Emscripten browser and JavaScript environment.</summary>
    Emscripten,

    /// <summary>The WebAssembly system interface.</summary>
    Wasi,
}

/// <summary>Represents an exact or bounded version requirement.</summary>
public sealed class VersionConstraint
{
    /// <summary>Initializes a version constraint.</summary>
    /// <param name="exact">The exact required version.</param>
    /// <param name="minimumInclusive">The inclusive lower bound.</param>
    /// <param name="maximumExclusive">The exclusive upper bound.</param>
    /// <exception cref="ArgumentException">
    /// An exact version is combined with a range, or the range is empty.
    /// </exception>
    public VersionConstraint(
        Version? exact = null,
        Version? minimumInclusive = null,
        Version? maximumExclusive = null)
    {
        if (exact is null && minimumInclusive is null && maximumExclusive is null)
        {
            throw new ArgumentException("At least one exact or range version must be supplied.");
        }

        if (exact is not null && (minimumInclusive is not null || maximumExclusive is not null))
        {
            throw new ArgumentException("An exact version cannot be combined with a range.");
        }

        if (minimumInclusive is not null
            && maximumExclusive is not null
            && minimumInclusive >= maximumExclusive)
        {
            throw new ArgumentException("The minimum version must be lower than the exclusive maximum.");
        }

        Exact = exact;
        MinimumInclusive = minimumInclusive;
        MaximumExclusive = maximumExclusive;
    }

    /// <summary>Gets the required exact version.</summary>
    public Version? Exact { get; }

    /// <summary>Gets the inclusive lower bound.</summary>
    public Version? MinimumInclusive { get; }

    /// <summary>Gets the exclusive upper bound.</summary>
    public Version? MaximumExclusive { get; }

    /// <summary>Determines whether a version satisfies this constraint.</summary>
    /// <param name="version">The version to inspect.</param>
    /// <returns>True when the version satisfies this constraint; otherwise, false.</returns>
    public bool Matches(Version? version)
    {
        if (version is null)
        {
            return false;
        }

        if (Exact is not null)
        {
            return version == Exact;
        }

        return (MinimumInclusive is null || version >= MinimumInclusive)
            && (MaximumExclusive is null || version < MaximumExclusive);
    }
}
