using System.Text.RegularExpressions;
using Incant.Base;

namespace Incant.Core.Toolchains;

/// <summary>Discovers standalone LLVM compiler toolchains.</summary>
public sealed partial class LlvmDiscoveryProvider : IDiscoveryProvider
{
    private static readonly IReadOnlyCollection<Kind> s_kinds = Array.AsReadOnly(
        [Kind.Llvm]);

    /// <inheritdoc />
    public string Name => "LLVM";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds => s_kinds;

    /// <inheritdoc />
    public async ValueTask<DiscoveryResult> DiscoverAsync(
        DiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var candidates = new List<Candidate>();
        var seenPaths = new HashSet<string>(ProviderUtilities.GetPathComparer());
        foreach (PathHint hint in context.GetExplicitPaths(Kind.Llvm))
        {
            AddCompilerCandidates(candidates, seenPaths, hint.Path, Source.Explicit);
        }

        AddEnvironmentCandidates(context, candidates, seenPaths);
        foreach (string directory in GetStandardDirectories(context))
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
                    "The LLVM candidate did not provide a complete standalone toolchain.",
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
        string? compiler = FindPrimaryCompiler(candidate.Path);
        if (compiler is null)
        {
            return null;
        }

        string? cppCompiler = FindRelatedExecutable(compiler, "clang++");
        if (cppCompiler is null && context.HostOS == PlatformOS.Windows)
        {
            cppCompiler = compiler;
        }

        string? archiver = FindRelatedExecutable(compiler, "llvm-ar", "llvm-lib");
        string? linker = FindRelatedExecutable(
            compiler,
            "ld.lld",
            "lld-link",
            "wasm-ld",
            "lld");
        if (cppCompiler is null || archiver is null || linker is null)
        {
            return null;
        }

        ProcessResult? versionResult = await ProviderUtilities.TryRunProbeAsync(
            compiler,
            ["--version"],
            context,
            cancellationToken).ConfigureAwait(false);
        if (versionResult is null)
        {
            return null;
        }

        string versionText = versionResult.StandardOutput + versionResult.StandardError;
        if (!versionText.Contains("clang", StringComparison.OrdinalIgnoreCase)
            || versionText.Contains("Apple clang", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ProcessResult? targetResult = await ProviderUtilities.TryRunProbeAsync(
            compiler,
            ["-dumpmachine"],
            context,
            cancellationToken).ConfigureAwait(false);
        ProcessResult? resourceResult = await ProviderUtilities.TryRunProbeAsync(
            compiler,
            ["-print-resource-dir"],
            context,
            cancellationToken).ConfigureAwait(false);
        string? targetTriple = targetResult?.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(targetTriple))
        {
            targetTriple = versionText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("Target:", StringComparison.OrdinalIgnoreCase))
                ?["Target:".Length..]
                .Trim();
        }

        if (string.IsNullOrWhiteSpace(targetTriple))
        {
            return null;
        }

        TargetArchitecture architecture = ProviderUtilities.ParseArchitecture(targetTriple);
        TargetPlatform platform = ParseTargetPlatform(targetTriple);
        var components = new List<Component>
        {
            new(ComponentKind.Compiler, compiler, context.HostArchitecture, architecture),
            new(ComponentKind.CppCompiler, cppCompiler, context.HostArchitecture, architecture),
            new(ComponentKind.Archiver, archiver, context.HostArchitecture, architecture),
            new(ComponentKind.Linker, linker, context.HostArchitecture, architecture),
        };
        string? clangClCompiler = FindRelatedExecutable(compiler, "clang-cl");
        if (clangClCompiler is not null)
        {
            components.Add(new Component(
                ComponentKind.Compiler,
                clangClCompiler,
                context.HostArchitecture,
                architecture));
        }

        string? resourceDirectory = resourceResult?.StandardOutput.Trim();
        if (!string.IsNullOrWhiteSpace(resourceDirectory) && Directory.Exists(resourceDirectory))
        {
            components.Add(new Component(
                ComponentKind.ResourceDirectory,
                resourceDirectory,
                context.HostArchitecture,
                architecture));
        }

        Version? version = ProviderUtilities.ParseVersion(versionText);
        return new Installation(
            Kind.Llvm,
            CompilerFamily.Clang,
            Path.GetDirectoryName(compiler)!,
            context.HostOS,
            context.HostArchitecture,
            version,
            version,
            ProviderUtilities.GetChannel(compiler, versionText),
            candidate.Sources,
            platform == TargetPlatform.Unknown ? [] : [platform],
            architecture == TargetArchitecture.Unknown ? [] : [architecture],
            components,
            targetTriple);
    }

    private static void AddEnvironmentCandidates(
        DiscoveryContext context,
        ICollection<Candidate> candidates,
        ISet<string> seenPaths)
    {
        string? llvmPath = context.GetEnvironmentVariable("LLVM_PATH");
        if (!string.IsNullOrWhiteSpace(llvmPath))
        {
            AddCompilerCandidates(candidates, seenPaths, llvmPath, Source.Environment);
        }

        string? visualStudioPath = context.GetEnvironmentVariable("VSINSTALLDIR");
        if (!string.IsNullOrWhiteSpace(visualStudioPath))
        {
            foreach (string directory in GetVisualStudioLlvmDirectories(visualStudioPath))
            {
                AddCompilerCandidates(
                    candidates,
                    seenPaths,
                    directory,
                    Source.Environment);
            }
        }

        string? compiler = ResolveEnvironmentExecutable(context, "CC");
        ProviderUtilities.AddCandidate(candidates, seenPaths, compiler, Source.Environment);

        string? cppCompiler = ResolveEnvironmentExecutable(context, "CXX");
        string? compilerFromCpp = cppCompiler is null ? null : FindCompilerForCpp(cppCompiler);
        ProviderUtilities.AddCandidate(
            candidates,
            seenPaths,
            compilerFromCpp,
            Source.Environment);
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
        int marker = fileName.LastIndexOf("clang++", StringComparison.OrdinalIgnoreCase);
        string? compilerName = marker >= 0
            ? fileName[..marker] + "clang" + fileName[(marker + "clang++".Length)..]
            : string.Equals(fileName, "clang-cl", StringComparison.OrdinalIgnoreCase)
                ? fileName
                : string.Equals(fileName, "c++", StringComparison.Ordinal)
                    ? "cc"
                    : null;
        if (compilerName is null)
        {
            return null;
        }

        string extension = Path.GetExtension(cppCompiler);
        string compiler = Path.Combine(
            Path.GetDirectoryName(cppCompiler)!,
            compilerName + extension);
        return File.Exists(compiler) ? compiler : null;
    }

    private static string? FindPrimaryCompiler(string candidate)
    {
        string fileName = Path.GetFileNameWithoutExtension(candidate);
        if (fileName.Equals("clang-cl", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("clang-cl-", StringComparison.OrdinalIgnoreCase))
        {
            // Keep clang-cl discoverable without making the MSVC-compatible driver the primary C compiler.
            return FindRelatedExecutable(candidate, "clang");
        }

        return File.Exists(candidate) ? candidate : null;
    }

    private static TargetPlatform ParseTargetPlatform(string targetTriple)
    {
        if (targetTriple.Contains("windows", StringComparison.OrdinalIgnoreCase)
            && targetTriple.Contains("msvc", StringComparison.OrdinalIgnoreCase))
        {
            return TargetPlatform.Windows;
        }

        if (targetTriple.Contains("darwin", StringComparison.OrdinalIgnoreCase))
        {
            return TargetPlatform.MacOS;
        }

        return targetTriple.Contains("linux", StringComparison.OrdinalIgnoreCase)
            && !targetTriple.Contains("android", StringComparison.OrdinalIgnoreCase)
            ? TargetPlatform.Linux
            : TargetPlatform.Unknown;
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

        foreach (string directory in new[] { path, Path.Combine(path, "bin") })
        {
            foreach (string compiler in ProviderUtilities.EnumerateFiles(
                directory,
                file => IsCompilerName(Path.GetFileName(file))))
            {
                ProviderUtilities.AddCandidate(candidates, seenPaths, compiler, source);
            }
        }
    }

    private static IEnumerable<string> GetStandardDirectories(DiscoveryContext context)
    {
        if (context.HostOS == PlatformOS.Windows)
        {
            string? programFiles = context.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, "LLVM", "bin");
                string visualStudioRoot = Path.Combine(programFiles, "Microsoft Visual Studio");
                foreach (string directory in EnumerateVisualStudioInstallations(visualStudioRoot)
                    .SelectMany(GetVisualStudioLlvmDirectories))
                {
                    yield return directory;
                }
            }
        }
        else
        {
            yield return "/usr/bin";
            yield return "/usr/local/bin";
            yield return "/opt/homebrew/opt/llvm/bin";
            yield return "/usr/local/opt/llvm/bin";
        }
    }

    private static IReadOnlyList<string> EnumerateVisualStudioInstallations(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(root)
                .SelectMany(Directory.EnumerateDirectories)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> GetVisualStudioLlvmDirectories(string visualStudioRoot)
    {
        yield return Path.Combine(visualStudioRoot, "VC", "Tools", "Llvm", "bin");
        yield return Path.Combine(visualStudioRoot, "VC", "Tools", "Llvm", "x64", "bin");
        yield return Path.Combine(visualStudioRoot, "VC", "Tools", "Llvm", "ARM64", "bin");
    }

    private static bool IsCompilerName(string fileName) => CompilerNamePattern().IsMatch(fileName);

    private static string? FindRelatedExecutable(string compiler, params string[] names)
    {
        string compilerName = Path.GetFileNameWithoutExtension(compiler);
        string suffix = compilerName.StartsWith("clang-cl", StringComparison.OrdinalIgnoreCase)
            ? compilerName["clang-cl".Length..]
            : compilerName.StartsWith("clang", StringComparison.OrdinalIgnoreCase)
                ? compilerName["clang".Length..]
                : string.Empty;
        string[] candidates = names
            .SelectMany(name => string.IsNullOrEmpty(suffix) ? [name] : new[] { name + suffix, name })
            .ToArray();
        return ProviderUtilities.FindSiblingExecutable(compiler, candidates);
    }

    [GeneratedRegex(@"^(?:clang|clang-cl)(?:-\d+(?:\.\d+)*)?(?:\.exe)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompilerNamePattern();
}
