using System.Text.RegularExpressions;
using Incant.Base;

namespace Incant.Core.Toolchains;

/// <summary>Discovers host GNU compiler toolchains.</summary>
public sealed partial class GnuDiscoveryProvider : IDiscoveryProvider
{
    private static readonly IReadOnlyCollection<Kind> s_kinds = Array.AsReadOnly(
        [Kind.Gnu]);

    /// <inheritdoc />
    public string Name => "GNU";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds => s_kinds;

    /// <inheritdoc />
    public async ValueTask<DiscoveryResult> DiscoverAsync(
        DiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.HostOS == PlatformOS.Windows)
        {
            return new DiscoveryResult();
        }

        var candidates = new List<Candidate>();
        var seenPaths = new HashSet<string>(ProviderUtilities.GetPathComparer());
        foreach (PathHint hint in context.GetExplicitPaths(Kind.Gnu))
        {
            AddCompilerCandidates(candidates, seenPaths, hint.Path, Source.Explicit);
        }

        AddEnvironmentCompiler(context, candidates, seenPaths, "CC");
        AddEnvironmentCppCompiler(context, candidates, seenPaths);
        foreach (string directory in GetStandardDirectories())
        {
            AddCompilerCandidates(candidates, seenPaths, directory, Source.StandardPath);
        }

        foreach (string directory in ProviderUtilities.GetPathDirectories(context))
        {
            AddCompilerCandidates(candidates, seenPaths, directory, Source.Path);
        }

        var installations = new List<Installation>();
        var diagnostics = new List<Diagnostic>();
        foreach (Candidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Installation? installation = await InspectAsync(
                candidate,
                context,
                cancellationToken).ConfigureAwait(false);
            if (installation is null)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "invalid-candidate",
                    Name,
                    "The GCC candidate did not provide a complete compiler, C++ compiler, and archiver toolchain.",
                    candidate.Path));
                continue;
            }

            installations.Add(installation);
        }

        return new DiscoveryResult(installations, diagnostics: diagnostics);
    }

    private static async Task<Installation?> InspectAsync(
        Candidate candidate,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        string compiler = candidate.Path;
        string? cppCompiler = FindRelatedExecutable(compiler, "g++");
        string? archiver = FindRelatedExecutable(compiler, "gcc-ar")
            ?? FindRelatedExecutable(compiler, "ar");
        if (!File.Exists(compiler) || cppCompiler is null || archiver is null)
        {
            return null;
        }

        ProcessResult? identityResult = await ProviderUtilities.TryRunProbeAsync(
            compiler,
            ["--version"],
            context,
            cancellationToken).ConfigureAwait(false);
        if (identityResult is null
            || (identityResult.StandardOutput + identityResult.StandardError)
                .Contains("clang", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ProcessResult? versionResult = await ProviderUtilities.TryRunProbeAsync(
            compiler,
            ["-dumpfullversion", "-dumpversion"],
            context,
            cancellationToken).ConfigureAwait(false);
        if (versionResult is null)
        {
            versionResult = await ProviderUtilities.TryRunProbeAsync(
                compiler,
                ["-dumpversion"],
                context,
                cancellationToken).ConfigureAwait(false);
        }

        ProcessResult? targetResult = await ProviderUtilities.TryRunProbeAsync(
            compiler,
            ["-dumpmachine"],
            context,
            cancellationToken).ConfigureAwait(false);
        if (versionResult is null || targetResult is null)
        {
            return null;
        }

        string targetTriple = targetResult.StandardOutput.Trim();
        if (targetTriple.Contains("mingw", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ProcessResult? sysrootResult = await ProviderUtilities.TryRunProbeAsync(
            compiler,
            ["-print-sysroot"],
            context,
            cancellationToken).ConfigureAwait(false);
        TargetArchitecture architecture = ProviderUtilities.ParseArchitecture(targetTriple);
        TargetPlatform platform = targetTriple.Contains("apple", StringComparison.OrdinalIgnoreCase)
            ? TargetPlatform.MacOS
            : TargetPlatform.Linux;
        var components = new List<Component>
        {
            new(ComponentKind.Compiler, compiler, context.HostArchitecture, architecture),
            new(ComponentKind.CppCompiler, cppCompiler, context.HostArchitecture, architecture),
            new(ComponentKind.Archiver, archiver, context.HostArchitecture, architecture),
        };
        string? sysroot = sysrootResult?.StandardOutput.Trim();
        if (!string.IsNullOrWhiteSpace(sysroot) && Directory.Exists(sysroot))
        {
            components.Add(new Component(
                ComponentKind.Sysroot,
                sysroot,
                context.HostArchitecture,
                architecture));
        }

        Version? version = ProviderUtilities.ParseVersion(versionResult.StandardOutput);
        return new Installation(
            Kind.Gnu,
            CompilerFamily.Gcc,
            Path.GetDirectoryName(compiler)!,
            context.HostOS,
            context.HostArchitecture,
            version,
            version,
            ProviderUtilities.GetChannel(
                compiler,
                identityResult.StandardOutput + identityResult.StandardError),
            candidate.Sources,
            [platform],
            architecture == TargetArchitecture.Unknown ? [] : [architecture],
            components,
            targetTriple);
    }

    private static void AddEnvironmentCompiler(
        DiscoveryContext context,
        ICollection<Candidate> candidates,
        ISet<string> seenPaths,
        string name)
    {
        string? path = ResolveEnvironmentExecutable(context, name);
        if (path is null)
        {
            return;
        }

        ProviderUtilities.AddCandidate(candidates, seenPaths, path, Source.Environment);
    }

    private static void AddEnvironmentCppCompiler(
        DiscoveryContext context,
        ICollection<Candidate> candidates,
        ISet<string> seenPaths)
    {
        string? cppCompiler = ResolveEnvironmentExecutable(context, "CXX");
        string? compiler = cppCompiler is null ? null : FindCompilerForCpp(cppCompiler);
        ProviderUtilities.AddCandidate(candidates, seenPaths, compiler, Source.Environment);
    }

    private static string? ResolveEnvironmentExecutable(
        DiscoveryContext context,
        string name)
    {
        string? value = context.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Path.IsPathRooted(value)
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar)
                ? value
                : ProviderUtilities.FindExecutableOnPath(context, value);
    }

    private static string? FindCompilerForCpp(string cppCompiler)
    {
        string fileName = Path.GetFileNameWithoutExtension(cppCompiler);
        int marker = fileName.LastIndexOf("g++", StringComparison.Ordinal);
        string? compilerName = marker >= 0
            ? fileName[..marker] + "gcc" + fileName[(marker + 3)..]
            : string.Equals(fileName, "c++", StringComparison.Ordinal)
                ? "cc"
                : null;
        if (compilerName is null)
        {
            return null;
        }

        string compiler = Path.Combine(Path.GetDirectoryName(cppCompiler)!, compilerName);
        return File.Exists(compiler) ? compiler : null;
    }

    private static void AddCompilerCandidates(
        ICollection<Candidate> candidates,
        ISet<string> seenPaths,
        string path,
        Source source)
    {
        if (File.Exists(path))
        {
            if (IsCompilerName(Path.GetFileName(path)))
            {
                ProviderUtilities.AddCandidate(candidates, seenPaths, path, source);
            }

            return;
        }

        foreach (string directory in ExpandBinDirectories(path))
        {
            foreach (string compiler in ProviderUtilities.EnumerateFiles(
                directory,
                file => IsCompilerName(Path.GetFileName(file))))
            {
                ProviderUtilities.AddCandidate(candidates, seenPaths, compiler, source);
            }
        }
    }

    private static IEnumerable<string> ExpandBinDirectories(string path)
    {
        yield return path;
        yield return Path.Combine(path, "bin");
    }

    private static IEnumerable<string> GetStandardDirectories()
    {
        yield return "/usr/bin";
        yield return "/usr/local/bin";
        yield return "/opt/homebrew/bin";
    }

    private static string? FindRelatedExecutable(string compiler, string relatedName)
    {
        string fileName = Path.GetFileNameWithoutExtension(compiler);
        string suffix = fileName.StartsWith("gcc", StringComparison.Ordinal)
            ? fileName[3..]
            : fileName.EndsWith("-gcc", StringComparison.Ordinal)
                ? string.Empty
                : ExtractSuffix(fileName);
        string prefix = fileName.EndsWith("-gcc", StringComparison.Ordinal)
            ? fileName[..^3]
            : ExtractPrefix(fileName);
        string candidateName = prefix + relatedName + suffix;
        string candidate = Path.Combine(Path.GetDirectoryName(compiler)!, candidateName);
        if (OperatingSystem.IsWindows())
        {
            candidate += ".exe";
        }

        return File.Exists(candidate) ? candidate : null;
    }

    private static string ExtractPrefix(string fileName)
    {
        int gccIndex = fileName.LastIndexOf("gcc", StringComparison.Ordinal);
        return gccIndex > 0 ? fileName[..gccIndex] : string.Empty;
    }

    private static string ExtractSuffix(string fileName)
    {
        int gccIndex = fileName.LastIndexOf("gcc", StringComparison.Ordinal);
        return gccIndex >= 0 ? fileName[(gccIndex + 3)..] : string.Empty;
    }

    private static bool IsCompilerName(string fileName) => CompilerNamePattern().IsMatch(fileName);

    [GeneratedRegex(
        @"^(?:gcc(?:-\d+(?:\.\d+)*)?|[A-Za-z0-9_+.-]+-gcc(?:-\d+(?:\.\d+)*)?)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CompilerNamePattern();
}
