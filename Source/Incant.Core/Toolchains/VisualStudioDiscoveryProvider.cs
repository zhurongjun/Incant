using System.Text.Json;
using Incant.Base;

namespace Incant.Core.Toolchains;

/// <summary>Discovers Visual Studio installations and their MSVC toolsets.</summary>
public sealed class VisualStudioDiscoveryProvider : IDiscoveryProvider
{
    private static readonly IReadOnlyCollection<Kind> s_kinds = Array.AsReadOnly(
        [Kind.VisualStudio]);

    /// <inheritdoc />
    public string Name => "Visual Studio";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds => s_kinds;

    /// <inheritdoc />
    public async ValueTask<DiscoveryResult> DiscoverAsync(
        DiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.HostOS != PlatformOS.Windows)
        {
            return new DiscoveryResult();
        }

        var candidates = new List<VisualStudioCandidate>();
        var seenPaths = new HashSet<string>(ProviderUtilities.GetPathComparer());
        foreach (PathHint hint in context.GetExplicitPaths(Kind.VisualStudio))
        {
            AddCandidate(candidates, seenPaths, hint.Path, Source.Explicit, productVersion: null);
        }

        AddCandidate(
            candidates,
            seenPaths,
            context.GetEnvironmentVariable("VSINSTALLDIR"),
            Source.Environment,
            ProviderUtilities.ParseVersion(context.GetEnvironmentVariable("VisualStudioVersion")));
        await AddVsWhereCandidatesAsync(candidates, seenPaths, context, cancellationToken).ConfigureAwait(false);
        AddStandardCandidates(candidates, seenPaths, context);

        var installations = new List<Installation>();
        var diagnostics = new List<Diagnostic>();
        foreach (VisualStudioCandidate candidate in candidates)
        {
            IReadOnlyList<Installation> toolsets = InspectCandidate(candidate, context);
            if (toolsets.Count == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "invalid-candidate",
                    Name,
                    "The Visual Studio candidate does not contain a usable MSVC toolset.",
                    candidate.Path));
            }
            else
            {
                installations.AddRange(toolsets);
            }
        }

        return new DiscoveryResult(installations, diagnostics: diagnostics);
    }

    private async Task AddVsWhereCandidatesAsync(
        ICollection<VisualStudioCandidate> candidates,
        ISet<string> seenPaths,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        string? programFilesX86 = context.GetEnvironmentVariable("ProgramFiles(x86)");
        if (string.IsNullOrWhiteSpace(programFilesX86))
        {
            return;
        }

        string vsWhere = Path.Combine(
            programFilesX86,
            "Microsoft Visual Studio",
            "Installer",
            "vswhere.exe");
        if (!File.Exists(vsWhere))
        {
            return;
        }

        ProcessResult? result = await ProviderUtilities.TryRunProbeAsync(
            vsWhere,
            [
                "-all",
                "-products",
                "*",
                "-requires",
                "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                "-format",
                "json",
                "-utf8",
            ],
            context,
            cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            foreach (JsonElement installation in document.RootElement.EnumerateArray())
            {
                if (!installation.TryGetProperty("installationPath", out JsonElement pathElement))
                {
                    continue;
                }

                Version? version = installation.TryGetProperty("installationVersion", out JsonElement versionElement)
                    ? ProviderUtilities.ParseVersion(versionElement.GetString())
                    : null;
                AddCandidate(
                    candidates,
                    seenPaths,
                    pathElement.GetString(),
                    Source.Vendor,
                    version);
            }
        }
        catch (JsonException)
        {
        }
    }

    private static IReadOnlyList<Installation> InspectCandidate(
        VisualStudioCandidate candidate,
        DiscoveryContext context)
    {
        IEnumerable<string> toolsetRoots = FindToolsetRoots(candidate.Path);
        var installations = new List<Installation>();
        foreach (string toolsetRoot in toolsetRoots)
        {
            Version? compilerVersion = ProviderUtilities.ParseVersion(Path.GetFileName(toolsetRoot));
            var components = new List<Component>();
            var targetArchitectures = new HashSet<TargetArchitecture>();
            foreach ((string hostName, TargetArchitecture hostArchitecture) in GetHostDirectories())
            {
                foreach ((string targetName, TargetArchitecture targetArchitecture) in GetTargetDirectories())
                {
                    string binDirectory = Path.Combine(toolsetRoot, "bin", hostName, targetName);
                    string compiler = Path.Combine(binDirectory, "cl.exe");
                    string linker = Path.Combine(binDirectory, "link.exe");
                    string archiver = Path.Combine(binDirectory, "lib.exe");
                    if (!File.Exists(compiler) || !File.Exists(linker) || !File.Exists(archiver))
                    {
                        continue;
                    }

                    targetArchitectures.Add(targetArchitecture);
                    components.Add(new Component(
                        ComponentKind.Compiler,
                        compiler,
                        hostArchitecture,
                        targetArchitecture));
                    components.Add(new Component(
                        ComponentKind.CppCompiler,
                        compiler,
                        hostArchitecture,
                        targetArchitecture));
                    components.Add(new Component(
                        ComponentKind.Linker,
                        linker,
                        hostArchitecture,
                        targetArchitecture));
                    components.Add(new Component(
                        ComponentKind.Archiver,
                        archiver,
                        hostArchitecture,
                        targetArchitecture));
                }
            }

            if (components.Count == 0)
            {
                continue;
            }

            installations.Add(new Installation(
                Kind.VisualStudio,
                CompilerFamily.Msvc,
                toolsetRoot,
                context.HostOS,
                context.HostArchitecture,
                candidate.ProductVersion,
                compilerVersion,
                ProviderUtilities.GetChannel(candidate.Path),
                candidate.Sources,
                [TargetPlatform.Windows],
                targetArchitectures,
                components));
        }

        return installations;
    }

    private static IEnumerable<string> FindToolsetRoots(string path)
    {
        if (!Directory.Exists(path))
        {
            return [];
        }

        if (Directory.Exists(Path.Combine(path, "bin"))
            && Directory.Exists(Path.Combine(path, "include")))
        {
            return [path];
        }

        string toolsRoot = Path.Combine(path, "VC", "Tools", "MSVC");
        if (!Directory.Exists(toolsRoot))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(toolsRoot).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void AddStandardCandidates(
        ICollection<VisualStudioCandidate> candidates,
        ISet<string> seenPaths,
        DiscoveryContext context)
    {
        foreach (string variableName in new[] { "ProgramFiles", "ProgramFiles(x86)" })
        {
            string? programFiles = context.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(programFiles))
            {
                continue;
            }

            string root = Path.Combine(programFiles, "Microsoft Visual Studio");
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (string versionDirectory in Directory.EnumerateDirectories(root))
                {
                    Version? productVersion = ParseProductVersionDirectory(
                        Path.GetFileName(versionDirectory));
                    if (productVersion is null)
                    {
                        continue;
                    }

                    foreach (string productDirectory in Directory.EnumerateDirectories(versionDirectory))
                    {
                        AddCandidate(
                            candidates,
                            seenPaths,
                            productDirectory,
                            Source.StandardPath,
                            productVersion);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void AddCandidate(
        ICollection<VisualStudioCandidate> candidates,
        ISet<string> seenPaths,
        string? path,
        Source source,
        Version? productVersion)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = ProviderUtilities.NormalizePath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (seenPaths.Add(fullPath))
        {
            candidates.Add(new VisualStudioCandidate(fullPath, source, productVersion));
        }
        else
        {
            candidates.FirstOrDefault(candidate =>
                ProviderUtilities.GetPathComparer().Equals(candidate.Path, fullPath))
                ?.Merge(source, productVersion);
        }
    }

    private static IEnumerable<(string Name, TargetArchitecture Architecture)> GetHostDirectories()
    {
        yield return ("Hostx64", TargetArchitecture.X64);
        yield return ("Hostarm64", TargetArchitecture.ARM64);
        yield return ("Hostx86", TargetArchitecture.X86);
    }

    private static Version? ParseProductVersionDirectory(string name) => name switch
    {
        "15" => new Version(15, 0),
        "16" => new Version(16, 0),
        "17" => new Version(17, 0),
        "18" => new Version(18, 0),
        "2017" => new Version(15, 0),
        "2019" => new Version(16, 0),
        "2022" => new Version(17, 0),
        "2026" => new Version(18, 0),
        _ => ProviderUtilities.ParseVersion(name),
    };

    private static IEnumerable<(string Name, TargetArchitecture Architecture)> GetTargetDirectories()
    {
        yield return ("x64", TargetArchitecture.X64);
        yield return ("arm64", TargetArchitecture.ARM64);
        yield return ("x86", TargetArchitecture.X86);
    }

    private sealed class VisualStudioCandidate
    {
        private readonly List<Source> _sources;

        internal VisualStudioCandidate(
            string path,
            Source source,
            Version? productVersion)
        {
            Path = path;
            ProductVersion = productVersion;
            _sources = [source];
        }

        internal string Path { get; }

        internal Version? ProductVersion { get; private set; }

        internal IReadOnlyList<Source> Sources => _sources;

        internal void Merge(Source source, Version? productVersion)
        {
            if (!_sources.Contains(source))
            {
                _sources.Add(source);
                _sources.Sort((left, right) =>
                    Resolver.GetSourcePriority(left).CompareTo(
                        Resolver.GetSourcePriority(right)));
            }

            ProductVersion ??= productVersion;
        }
    }
}
