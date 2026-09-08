using System.Collections.ObjectModel;
using Incant.CXLegacy;

namespace Incant.CXLegacy.FindSdk;

/// <summary>An immutable inventory of one SDK component. It is not a compiler/SDK pairing or a list of build flags.</summary>
public sealed class Sdk
{
    /// <summary>Copies identity, layouts and provenance. Platform SDK and compiler development files remain separate records.</summary>
    /// <param name="kind">The independent SDK component family.</param>
    /// <param name="rootPath">The absolute installation anchor.</param>
    /// <param name="layouts">Known target layouts; an empty collection retains only the installation identity.</param>
    /// <param name="version">The SDK component version, or null when unknown.</param>
    /// <param name="environmentPath">The containing product environment, defaulting to the installation anchor.</param>
    /// <param name="productVersion">The independently identified product version.</param>
    /// <param name="compilerPath">The absolute compiler identity path, when applicable.</param>
    /// <param name="channel">The installation release channel.</param>
    /// <param name="sources">Discovery provenance, copied into a read-only collection.</param>
    /// <param name="diagnostics">Installation-level observations, independent of target layout diagnostics.</param>
    /// <exception cref="ArgumentException">A path is empty or relative, or a collection contains null.</exception>
    /// <exception cref="ArgumentNullException">The layout collection is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is invalid.</exception>
    public Sdk(
        Kind kind,
        string rootPath,
        IEnumerable<TargetLayout> layouts,
        Version? version = null,
        string? environmentPath = null,
        Version? productVersion = null,
        string? compilerPath = null,
        Channel channel = Channel.Stable,
        IEnumerable<Source>? sources = null,
        IEnumerable<Diagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(layouts);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        Kind = kind;
        RootPath = Absolute(rootPath);
        EnvironmentPath = Absolute(environmentPath ?? rootPath);
        CompilerPath = compilerPath is null ? null : Absolute(compilerPath);
        Version = version;
        ProductVersion = productVersion;
        Channel = channel;
        Layouts = SearchPaths.Freeze(layouts);
        if (Layouts.Any(layout => layout is null))
        {
            throw new ArgumentException("Layouts cannot contain null.", nameof(layouts));
        }

        Sources = SearchPaths.Freeze((sources ?? [Source.StandardPath]).Distinct().Order());
        Diagnostics = SearchPaths.Freeze(diagnostics);
        if (Diagnostics.Any(diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Diagnostics cannot contain null.", nameof(diagnostics));
        }
    }

    /// <summary>Gets the component family.</summary>
    public Kind Kind { get; }

    /// <summary>Gets the installation anchor; resources may live outside it.</summary>
    public string RootPath { get; }

    /// <summary>Gets the containing product/developer environment.</summary>
    public string EnvironmentPath { get; }

    /// <summary>Gets the SDK component's own version, or null when unknown.</summary>
    public Version? Version { get; }

    /// <summary>Gets the enclosing product version, independently of the SDK version.</summary>
    public Version? ProductVersion { get; }

    /// <summary>Gets the preserved compiler invocation entry associated with a development-file inventory, if any.</summary>
    public string? CompilerPath { get; }

    /// <summary>Gets the release channel.</summary>
    public Channel Channel { get; }

    /// <summary>Gets target-specific layouts; API availability is never unioned across architectures.</summary>
    public IReadOnlyList<TargetLayout> Layouts { get; }

    /// <summary>Gets discovery sources in priority order.</summary>
    public IReadOnlyList<Source> Sources { get; }

    /// <summary>Gets installation-level missing-resource and probe diagnostics; target observations belong to each layout.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    internal Sdk WithLayouts(IEnumerable<TargetLayout> layouts, IEnumerable<Source> sources, IEnumerable<Diagnostic>? diagnostics = null) =>
        new(Kind, RootPath, layouts, Version, EnvironmentPath, ProductVersion, CompilerPath, Channel, sources, diagnostics ?? Diagnostics);

    internal static string Absolute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("SDK resource paths must be absolute.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Incant.Internal.FileSystemPath.Absolute(path));
    }
}

/// <summary>A target-specific installed layout. Resource order is significant within each purpose/language.</summary>
public sealed class TargetLayout
{
    /// <summary>Copies target metadata and ordered resources. API aliases map requested levels to installed library levels.</summary>
    /// <param name="platform">The platform established by installation metadata or compiler probes.</param>
    /// <param name="architecture">The established architecture; Unknown never implies known target support.</param>
    /// <param name="resources">Existing resources in significant search order.</param>
    /// <param name="targetTriple">The effective target identity, retaining ABI and deployment distinctions.</param>
    /// <param name="sysrootPath">The absolute installed sysroot, when available.</param>
    /// <param name="apiLevels">Installed positive Android API levels for this ABI.</param>
    /// <param name="apiAliases">Aliases backed by this layout's installed API levels.</param>
    /// <param name="multilib">The SDK-local installed library variant identifier, not compiler arguments.</param>
    /// <param name="minimumDeploymentVersion">The metadata's minimum deployment version.</param>
    /// <param name="defaultDeploymentVersion">The metadata's suggested deployment version.</param>
    /// <param name="diagnostics">Observations specific to this target and library variant.</param>
    /// <exception cref="ArgumentException">A collection item is null, target metadata is empty or invalid, or the sysroot path is not absolute.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A platform or architecture value is invalid.</exception>
    public TargetLayout(
        TargetPlatform platform,
        TargetArchitecture architecture,
        IEnumerable<Resource>? resources = null,
        string? targetTriple = null,
        string? sysrootPath = null,
        IEnumerable<int>? apiLevels = null,
        IReadOnlyDictionary<int, int>? apiAliases = null,
        string? multilib = null,
        Version? minimumDeploymentVersion = null,
        Version? defaultDeploymentVersion = null,
        IEnumerable<Diagnostic>? diagnostics = null)
    {
        if (!Enum.IsDefined(platform))
        {
            throw new ArgumentOutOfRangeException(nameof(platform));
        }

        if (!Enum.IsDefined(architecture))
        {
            throw new ArgumentOutOfRangeException(nameof(architecture));
        }

        if (targetTriple is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetTriple);
        }

        if (multilib is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(multilib);
        }

        Multilib = multilib;
        MinimumDeploymentVersion = minimumDeploymentVersion;
        DefaultDeploymentVersion = defaultDeploymentVersion;
        Platform = platform;
        Architecture = architecture;
        TargetTriple = targetTriple;
        SysrootPath = sysrootPath is null ? null : Sdk.Absolute(sysrootPath);
        Resources = SearchPaths.Freeze(resources);
        Diagnostics = SearchPaths.Freeze(diagnostics);
        ApiLevels = SearchPaths.Freeze((apiLevels ?? []).Distinct().Order());
        ApiAliases = new ReadOnlyDictionary<int, int>(new Dictionary<int, int>(apiAliases ?? new Dictionary<int, int>()));
        if (Resources.Any(resource => resource is null))
        {
            throw new ArgumentException("Resources cannot contain null.", nameof(resources));
        }

        if (Diagnostics.Any(diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Diagnostics cannot contain null.", nameof(diagnostics));
        }

        if (ApiLevels.Any(level => level <= 0))
        {
            throw new ArgumentException("API levels must be positive.", nameof(apiLevels));
        }

        if (ApiAliases.Any(alias => alias.Key <= 0 || !ApiLevels.Contains(alias.Value)))
        {
            throw new ArgumentException("API aliases must reference installed positive levels.", nameof(apiAliases));
        }
    }

    /// <summary>
    /// Gets the installed library variant identifier: a GCC multilib, Emscripten library directory,
    /// or WASI exception layout. A dot identifies the default variant (noeh for classified WASI layouts);
    /// eh identifies the WASI exception variant. Null means no variant classification.
    /// </summary>
    public string? Multilib { get; }

    /// <summary>Gets the SDK metadata's minimum deployment version, without selecting a build deployment target.</summary>
    public Version? MinimumDeploymentVersion { get; }

    /// <summary>Gets the SDK metadata's suggested deployment version, independently of the SDK version.</summary>
    public Version? DefaultDeploymentVersion { get; }

    /// <summary>Gets the target platform; device and simulator values are distinct.</summary>
    public TargetPlatform Platform { get; }

    /// <summary>Gets the target architecture; Unknown does not claim support for a known architecture.</summary>
    public TargetArchitecture Architecture { get; }

    /// <summary>Gets the associated compiler target triple, if known.</summary>
    public string? TargetTriple { get; }

    /// <summary>Gets the actual sysroot, or null for a non-sysroot layout such as native Windows.</summary>
    public string? SysrootPath { get; }

    /// <summary>Gets the ordered resource inventory, including external references.</summary>
    public IReadOnlyList<Resource> Resources { get; }

    /// <summary>Gets missing-resource and probe observations for this target and variant.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Gets API levels available for this architecture.</summary>
    public IReadOnlyList<int> ApiLevels { get; }

    /// <summary>Gets metadata aliases backed by installed library levels in this layout.</summary>
    public IReadOnlyDictionary<int, int> ApiAliases { get; }
}

/// <summary>An installed directory or file, retaining purpose and ownership instead of becoming a build flag.</summary>
public sealed record Resource
{
    /// <summary>Initializes a resource. External references belong to another installation.</summary>
    /// <param name="purpose">The file or directory classification.</param>
    /// <param name="path">The absolute installed resource path.</param>
    /// <param name="isExternal">Whether another component owns the resource.</param>
    /// <param name="apiLevel">The positive installed Android API level, or null for a shared resource.</param>
    /// <exception cref="ArgumentException">The path is empty or relative.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The purpose or API level is invalid.</exception>
    public Resource(ResourcePurpose purpose, string path, bool isExternal = false, int? apiLevel = null)
    {
        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose));
        }

        if (apiLevel is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(apiLevel));
        }

        ApiLevel = apiLevel;
        Purpose = purpose;
        Path = Sdk.Absolute(path);
        IsExternal = isExternal;
    }

    /// <summary>Gets the installed Android library API level, or null for API-independent resources.</summary>
    public int? ApiLevel { get; }

    /// <summary>Gets the resource's purpose, including its source language for headers.</summary>
    public ResourcePurpose Purpose { get; }

    /// <summary>Gets the absolute path.</summary>
    public string Path { get; }

    /// <summary>Gets whether the resource is referenced rather than owned by this SDK component.</summary>
    public bool IsExternal { get; }

    /// <summary>Gets whether the path denotes a directory rather than a library/startup file.</summary>
    public bool IsDirectory => Purpose is not ResourcePurpose.Library and not ResourcePurpose.Startup;
}

/// <summary>SDK components are separate from executable toolset families.</summary>
public enum Kind
{
    /// <summary>Windows Kit platform development files.</summary>
    Windows,

    /// <summary>MSVC headers and runtime libraries.</summary>
    Msvc,

    /// <summary>An Apple platform SDK, including macOS and device/simulator SDKs.</summary>
    Apple,

    /// <summary>AppleClang development files.</summary>
    AppleClang,

    /// <summary>GNU compiler headers and runtime/standard-library files.</summary>
    Gnu,

    /// <summary>LLVM compiler resources and supplied runtime/standard-library files.</summary>
    Llvm,

    /// <summary>Native Linux system development files.</summary>
    Linux,

    /// <summary>An explicitly selected or compiler-reported cross sysroot.</summary>
    Sysroot,

    /// <summary>Android platform files in a particular NDK revision.</summary>
    AndroidNdk,

    /// <summary>Installed Emscripten sysroot files.</summary>
    Emscripten,

    /// <summary>WASI sysroot files.</summary>
    WasiSdk,
}

/// <summary>Resource classifications; C/C++ headers and Frameworks intentionally remain distinct.</summary>
public enum ResourcePurpose
{
    /// <summary>A C header search directory.</summary>
    CInclude,

    /// <summary>A C++ header search directory.</summary>
    CppInclude,

    /// <summary>A compiler resource directory containing builtins and possibly runtime files.</summary>
    Builtin,

    /// <summary>A library search directory.</summary>
    LibraryDirectory,

    /// <summary>A compiler or language runtime directory.</summary>
    RuntimeDirectory,

    /// <summary>An identified library, including Apple .tbd stubs.</summary>
    Library,

    /// <summary>A startup object file.</summary>
    Startup,

    /// <summary>A Framework search directory, not a normal header directory.</summary>
    Framework,
}
