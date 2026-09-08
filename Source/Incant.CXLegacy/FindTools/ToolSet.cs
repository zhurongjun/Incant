using Incant.Base;
using Incant.CXLegacy;

namespace Incant.CXLegacy.FindTools;

/// <summary>A concrete-version environment that resolves individual tools on demand, without SDK pairing or caching.</summary>
public abstract class ToolSet
{
    /// <summary>Initializes immutable identity and discovery metadata. Paths must be absolute.</summary>
    protected ToolSet(
        Kind kind,
        string rootPath,
        string environmentPath,
        Version? version = null,
        Version? productVersion = null,
        Version? compilerVersion = null,
        string? compilerPath = null,
        string? defaultTargetTriple = null,
        Channel channel = Channel.Stable,
        IEnumerable<Source>? sources = null,
        IEnumerable<Diagnostic>? diagnostics = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        Kind = kind;
        RootPath = RequireAbsolute(rootPath);
        EnvironmentPath = RequireAbsolute(environmentPath);
        CompilerPath = compilerPath is null ? null : RequireAbsolute(compilerPath);
        Version = version;
        ProductVersion = productVersion;
        CompilerVersion = compilerVersion;
        DefaultTargetTriple = defaultTargetTriple;
        Channel = channel;
        Sources = SearchPaths.Freeze((sources ?? [Source.StandardPath]).Distinct().Order());
        Diagnostics = SearchPaths.Freeze(diagnostics);
    }

    /// <summary>Gets the installation family.</summary>
    public Kind Kind { get; }

    /// <summary>Gets this concrete version's installation anchor.</summary>
    public string RootPath { get; }

    /// <summary>Gets its enclosing product or developer environment.</summary>
    public string EnvironmentPath { get; }

    /// <summary>Gets the toolset version, which may differ from the compiler version.</summary>
    public Version? Version { get; }

    /// <summary>Gets the enclosing product version, such as Visual Studio or Xcode.</summary>
    public Version? ProductVersion { get; }

    /// <summary>Gets the compiler's own version, when established.</summary>
    public Version? CompilerVersion { get; }

    /// <summary>Gets the compiler invocation path when probing established this environment; wrappers and stable package-manager entries are preserved.</summary>
    public string? CompilerPath { get; }

    /// <summary>Gets the compiler-reported default target, without claiming support for every target.</summary>
    public string? DefaultTargetTriple { get; }

    /// <summary>Gets the release channel.</summary>
    public Channel Channel { get; }

    /// <summary>Gets all discovery sources in priority order.</summary>
    public IReadOnlyList<Source> Sources { get; }

    /// <summary>Gets discovery diagnostics.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Gets the current host OS. Finding a tool does not change the host environment.</summary>
    public PlatformOS HostOS => Platform.OS;

    /// <summary>Finds a tool role in this installation, honoring its target, version and package naming. Missing tools return null; no SDK or other installation is searched.</summary>
    /// <param name="name">A single tool role name without directory separators.</param>
    /// <param name="query">Optional host and target constraints.</param>
    /// <param name="cancellationToken">Cancels the fresh tool lookup.</param>
    /// <returns>The tool in this installation, or null when it is missing or does not match.</returns>
    /// <exception cref="ArgumentException">The name or a query enum value is invalid.</exception>
    /// <exception cref="OperationCanceledException">Lookup is canceled.</exception>
    public Task<Tool?> FindToolAsync(string name, ToolQuery? query = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(['/', '\\', '\0']) >= 0 || name is "." or "..")
        {
            throw new ArgumentException("A tool name must be a single executable name.", nameof(name));
        }

        query ??= new ToolQuery();
        if (query.HostArchitecture is TargetArchitecture host && !Enum.IsDefined(host)
            || query.TargetArchitecture is TargetArchitecture target && !Enum.IsDefined(target)
            || query.TargetPlatform is TargetPlatform platform && !Enum.IsDefined(platform))
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return FindToolCoreAsync(name, query, cancellationToken);
    }

    /// <summary>Synchronously waits for <see cref="FindToolAsync"/>.</summary>
    /// <inheritdoc cref="FindToolAsync"/>
    public Tool? FindTool(string name, ToolQuery? query = null, CancellationToken cancellationToken = default) =>
        FindToolAsync(name, query, cancellationToken).GetAwaiter().GetResult();

    /// <summary>Implements lookup inside this concrete environment. Implementations must not cache results.</summary>
    protected abstract Task<Tool?> FindToolCoreAsync(string name, ToolQuery query, CancellationToken cancellationToken);

    internal ToolSet WithSources(IEnumerable<Source> sources) => new SourcedToolSet(this, sources);

    private static string RequireAbsolute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Installation paths must be absolute.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Incant.Internal.FileSystemPath.Absolute(path));
    }

    private sealed class SourcedToolSet(ToolSet original, IEnumerable<Source> sources)
        : ToolSet(original.Kind, original.RootPath, original.EnvironmentPath, original.Version,
            original.ProductVersion, original.CompilerVersion, original.CompilerPath,
            original.DefaultTargetTriple, original.Channel, sources, original.Diagnostics)
    {
        protected override Task<Tool?> FindToolCoreAsync(string name, ToolQuery query, CancellationToken cancellationToken) =>
            original.FindToolAsync(name, query, cancellationToken);
    }
}

/// <summary>Identifies an installation family, not a compiler or linker role.</summary>
public enum Kind
{
    /// <summary>A concrete MSVC toolset in Visual Studio.</summary>
    VisualStudio,

    /// <summary>A GNU compiler installation.</summary>
    Gnu,

    /// <summary>A standalone LLVM installation.</summary>
    Llvm,

    /// <summary>An Xcode toolchain or Command Line Tools installation.</summary>
    Xcode,

    /// <summary>An Android NDK installation.</summary>
    AndroidNdk,

    /// <summary>An Emscripten installation.</summary>
    Emscripten,

    /// <summary>A WASI SDK compiler installation.</summary>
    WasiSdk,
}

internal sealed class DirectoryToolSet : ToolSet
{
    private readonly string _bin;

    private readonly bool _allowsWrappers;

    private readonly DiscoveryContext _context;

    internal DirectoryToolSet(Kind kind, string root, string environment, string bin,
        Version? version, Version? productVersion, Version? compilerVersion, string? compiler,
        string? triple, Channel channel, IEnumerable<Source> sources, DiscoveryContext context,
        bool allowsWrappers = false, IEnumerable<Diagnostic>? diagnostics = null)
        : base(kind, root, environment, version, productVersion, compilerVersion, compiler, triple, channel, sources, diagnostics)
    {
        _bin = bin;
        _allowsWrappers = allowsWrappers;
        _context = context;
    }

    protected override async Task<Tool?> FindToolCoreAsync(
        string name,
        ToolQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? path = SearchPaths.Executable(
            _bin,
            name,
            _allowsWrappers);
        if (path is null && Kind == Kind.Emscripten
            && (name.StartsWith("clang", StringComparison.Ordinal)
                || name.StartsWith("llvm-", StringComparison.Ordinal)
                || name == "wasm-ld"))
        {
            path = SearchPaths.Executable(
                Path.GetFullPath(Path.Combine(RootPath, "..", "bin")),
                name,
                wrappers: true);
        }

        if (path is null)
        {
            return null;
        }

        TargetArchitecture host =
            await HostExecutableInspector.SelectAsync(
                path,
                query.HostArchitecture,
                _context,
                _allowsWrappers,
                cancellationToken).ConfigureAwait(false);
        if (query.HostArchitecture is not null
            && host == TargetArchitecture.Unknown)
        {
            return null;
        }

        return new Tool(name, path, host);
    }
}
