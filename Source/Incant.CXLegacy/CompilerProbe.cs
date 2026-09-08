using Incant.Base;
using Incant.CXLegacy.FindSdk;

namespace Incant.CXLegacy;

/// <summary>One compiler identity and its read-only target queries. Each discovery opens a new instance.</summary>
internal sealed class CompilerProbe
{
    private readonly DiscoveryContext _context;

    private readonly string _probePath;

    private CompilerProbe(
        string path,
        string probePath,
        string resolvedPath,
        DiscoveryContext context,
        string identityText,
        string? defaultTriple,
        Version? version,
        CompilerFamily family,
        IReadOnlyList<ProbeOutcome> identityDiagnostics)
    {
        Path = path;
        _probePath = probePath;
        ResolvedPath = resolvedPath;
        Version = version;
        _context = context;
        IdentityText = identityText;
        Family = family;
        IdentityDiagnostics = identityDiagnostics;
        DefaultTarget = string.IsNullOrWhiteSpace(defaultTriple)
            ? null
            : new TargetIdentity(defaultTriple.Trim());
    }

    internal string Path { get; }

    internal string ResolvedPath { get; }

    internal string IdentityText { get; }

    internal CompilerFamily Family { get; }

    internal IReadOnlyList<ProbeOutcome> IdentityDiagnostics { get; }

    internal bool IsClang => Family is CompilerFamily.Llvm or CompilerFamily.AppleClang;

    internal bool IsApple => Family == CompilerFamily.AppleClang;

    internal TargetIdentity? DefaultTarget { get; }

    internal Version? Version { get; }

    internal string Prefix => System.IO.Path.GetDirectoryName(
        System.IO.Path.GetDirectoryName(ResolvedPath))!;

    internal static async Task<CompilerProbe?> OpenAsync(
        string path,
        DiscoveryContext context,
        CancellationToken cancellationToken) =>
        (await OpenDetailedAsync(path, context, cancellationToken).ConfigureAwait(false)).Probe;

    internal static async Task<CompilerOpenResult> OpenDetailedAsync(
        string path,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        string probePath = path;
        string resolvedPath = SearchPaths.InvocationIdentity(path);
        if (OperatingSystem.IsMacOS())
        {
            AppleLocator.CompilerResolution resolution =
                await AppleLocator.ResolveCompilerAsync(
                    path,
                    context,
                    cancellationToken).ConfigureAwait(false);
            if (resolution.ResolvedCompilerPath is null)
            {
                return new CompilerOpenResult(null, Error: "The Apple developer environment could not be resolved.");
            }

            resolvedPath = resolution.ResolvedCompilerPath;
            if (resolution.DeveloperPath is not null)
            {
                var environment = new Dictionary<string, string?>(context.Environment, SearchPaths.Comparer);
                foreach ((string name, string? value) in AppleLocator.Environment(resolution.DeveloperPath))
                {
                    environment[name] = value;
                }

                context = new DiscoveryContext(environment, context.ProbeTimeout);
            }
        }

        ProbeOutcome identityOutcome = await context.ProbeDetailedAsync(
            probePath, ["--version"], cancellationToken).ConfigureAwait(false);
        if (identityOutcome.Status != ProbeStatus.Success)
        {
            return new CompilerOpenResult(null, identityOutcome);
        }

        ProcessResult identity = identityOutcome.Result!;

        string text = identity.StandardOutput + identity.StandardError;
        CompilerFamily? family = Classify(text);
        if (family is null)
        {
            return new CompilerOpenResult(null, identityOutcome,
                identityOutcome.Describe("UnrecognizedIdentity"));
        }

        var identityDiagnostics = new List<ProbeOutcome>();
        async Task<ProcessResult?> InspectIdentityDetailAsync(params string[] arguments)
        {
            ProbeOutcome outcome = await context.ProbeDetailedAsync(probePath, arguments, cancellationToken)
                .ConfigureAwait(false);
            if (outcome.Status == ProbeStatus.Success)
            {
                return outcome.Result;
            }

            identityDiagnostics.Add(outcome);
            return null;
        }

        ProcessResult? machine = await InspectIdentityDetailAsync("-dumpmachine").ConfigureAwait(false);
        Version? version = SearchPaths.CompilerVersion(text);
        if (family == CompilerFamily.Gnu)
        {
            ProcessResult? reportedVersion = await InspectIdentityDetailAsync("-dumpfullversion", "-dumpversion")
                .ConfigureAwait(false);
            version = SearchPaths.Version(reportedVersion?.StandardOutput) ?? version;
        }

        string? defaultTriple = machine?.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(defaultTriple))
        {
            string? targetLine = text.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => line.StartsWith("Target:", StringComparison.OrdinalIgnoreCase));
            defaultTriple = targetLine?["Target:".Length..].Trim();
        }

        return new CompilerOpenResult(new CompilerProbe(
            path,
            probePath,
            resolvedPath,
            context,
            text,
            defaultTriple,
            version,
            family.Value,
            identityDiagnostics.ToArray()));
    }

    private static CompilerFamily? Classify(string identity) =>
        identity.Contains("Apple clang", StringComparison.OrdinalIgnoreCase) ? CompilerFamily.AppleClang
        : identity.Contains("clang", StringComparison.OrdinalIgnoreCase) ? CompilerFamily.Llvm
        : identity.Contains("gcc", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("g++", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Free Software Foundation", StringComparison.OrdinalIgnoreCase)
                ? CompilerFamily.Gnu : null;

    internal async Task<CompilerTargets> FindTargetsAsync(
        SdkQuery query,
        CancellationToken cancellationToken)
    {
        if (query.SysrootPath is not null && !Directory.Exists(query.SysrootPath))
        {
            throw new DiscoveryException($"The explicit sysroot '{query.SysrootPath}' does not exist.");
        }

        var diagnostics = new List<Diagnostic>();
        var variants = new List<(string? Name, string[] Flags)>();
        if (IsClang)
        {
            string[] flags = [];
            if (query.TargetArchitecture == TargetArchitecture.X86 && query.TargetTriple is null
                && DefaultTarget?.Architecture == TargetArchitecture.X64)
            {
                flags = ["-m32"];
            }

            variants.Add((query.Multilib, flags));
        }
        else
        {
            ProcessResult? reported = await _context.ProbeAsync(
                _probePath,
                ["-print-multi-lib"],
                cancellationToken).ConfigureAwait(false);
            foreach (string line in (reported?.StandardOutput ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = line.Split(';', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                string[]? flags = parts[1] switch
                {
                    "" => [],
                    "@m32" => ["-m32"],
                    "@m64" => ["-m64"],
                    "@mx32" => ["-mx32"],
                    _ => null,
                };
                if (flags is not null)
                {
                    variants.Add((parts[0], flags));
                }
            }

            if (variants.Count == 0)
            {
                variants.Add((".", []));
            }
        }

        Task<CompilerTargets>[] tasks = variants
            .Where(variant => query.Multilib is null || IsClang || variant.Name == query.Multilib)
            .Select(variant => InspectTargetAsync(query, variant.Name, variant.Flags, cancellationToken)).ToArray();
        CompilerTargets[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        CompilerTarget[] targets = results.SelectMany(result => result.Targets).ToArray();
        diagnostics.AddRange(results.SelectMany(result => result.Diagnostics));
        if (targets.Length == 0)
        {
            diagnostics.Add(Missing("The requested target or multilib could not be established from this compiler.", Path));
        }

        return new CompilerTargets(targets, diagnostics);
    }

    private async Task<CompilerTargets> InspectTargetAsync(
        SdkQuery query, string? multilib, string[] variantFlags, CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        var arguments = new List<string>(variantFlags);
        if (IsClang && query.TargetTriple is not null)
        {
            arguments.Add("--target=" + query.TargetTriple);
        }

        TargetPlatform platform = SearchPaths.Platform(query.TargetTriple ?? DefaultTarget?.Triple);
        if (query.SysrootPath is not null)
        {
            if (platform is TargetPlatform.MacOS or TargetPlatform.IOS or TargetPlatform.IOSSimulator
                or TargetPlatform.TvOS or TargetPlatform.TvOSSimulator or TargetPlatform.WatchOS
                or TargetPlatform.WatchOSSimulator or TargetPlatform.VisionOS or TargetPlatform.VisionOSSimulator)
            {
                arguments.AddRange(["-isysroot", query.SysrootPath]);
            }
            else
            {
                arguments.Add("--sysroot=" + query.SysrootPath);
            }
        }

        async Task<ProcessResult?> ProbeAsync(params string[] flags) =>
            await _context.ProbeAsync(
                _probePath,
                arguments.Concat(flags).ToArray(),
                cancellationToken).ConfigureAwait(false);

        async Task<ProcessResult?> RequiredProbeAsync(params string[] flags)
        {
            ProbeOutcome outcome = await _context.ProbeDetailedAsync(_probePath,
                arguments.Concat(flags).ToArray(), cancellationToken).ConfigureAwait(false);
            if (outcome.Status == ProbeStatus.Success)
            {
                return outcome.Result;
            }

            diagnostics.Add(Missing(outcome.Describe(), Path));
            return null;
        }

        ProcessResult? macros = await RequiredProbeAsync("-dM", "-E", "-x", "c", NullInput).ConfigureAwait(false);
        TargetArchitecture architecture = MacroArchitecture(macros?.StandardOutput);
        bool isX32 = architecture == TargetArchitecture.X64
            && (macros?.StandardOutput.Contains("#define __ILP32__", StringComparison.Ordinal) ?? false);
        string? triple = IsClang
            ? (await ProbeAsync("-print-target-triple").ConfigureAwait(false))?.StandardOutput.Trim()
            : DefaultTarget?.Triple;
        if (string.IsNullOrWhiteSpace(triple))
        {
            triple = IsClang && macros is not null ? query.TargetTriple ?? DefaultTarget?.Triple : DefaultTarget?.Triple;
        }

        if (triple is null)
        {
            return new CompilerTargets([], diagnostics);
        }

        var target = new TargetIdentity(triple);
        if (architecture != TargetArchitecture.Unknown)
        {
            target = target.WithArchitecture(architecture, isX32);
        }
        else if (variantFlags.Length > 0 || IsClang && query.TargetTriple is not null
            && !TargetIdentity.AreEquivalent(query.TargetTriple, DefaultTarget?.Triple))
        {
            return new CompilerTargets([], diagnostics);
        }

        if (query.TargetArchitecture is not null && query.TargetArchitecture != target.Architecture
            || query.TargetPlatform is not null && query.TargetPlatform != target.Platform
            || query.TargetTriple is not null && !TargetIdentity.AreEquivalent(query.TargetTriple, target.Triple))
        {
            return new CompilerTargets([], diagnostics);
        }

        string? multiarch = (await ProbeAsync("-print-multiarch").ConfigureAwait(false))?.StandardOutput.Trim();
        if (!IsSimpleDirectory(multiarch) || !target.HasSameAbi(new TargetIdentity(multiarch!)))
        {
            multiarch = null;
        }

        string? reportedMultilib = (await ProbeAsync("-print-multi-directory").ConfigureAwait(false))?.StandardOutput.Trim();
        if (IsClang)
        {
            if (query.Multilib is not null && query.Multilib != reportedMultilib)
            {
                return new CompilerTargets([], diagnostics);
            }

            multilib = string.IsNullOrWhiteSpace(reportedMultilib) ? null : reportedMultilib;
        }

        string? sysroot = query.SysrootPath
            ?? (await ProbeAsync("-print-sysroot").ConfigureAwait(false))?.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(sysroot))
        {
            sysroot = null;
        }
        else if (!System.IO.Path.IsPathFullyQualified(sysroot))
        {
            sysroot = null;
        }

        string? ExistingDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (!System.IO.Path.IsPathFullyQualified(path))
            {
                diagnostics.Add(Missing($"The driver reported a non-absolute resource path: {path}", Path));
                return null;
            }

            try
            {
                string normalized = SearchPaths.Normalize(path);
                if (Directory.Exists(normalized))
                {
                    return normalized;
                }

                diagnostics.Add(Missing($"The reported resource path does not exist after resolution: {normalized}", path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Missing(exception.Message, path));
            }

            return null;
        }

        var includes = new List<CompilerInclude>();
        foreach (string language in new[] { "c", "c++" })
        {
            ProcessResult? search = await RequiredProbeAsync("-E", "-x", language, "-v", NullInput).ConfigureAwait(false);
            bool isInSearch = false;
            int startCount = includes.Count;
            foreach (string line in (search?.StandardError + search?.StandardOutput).Split('\n'))
            {
                string value = line.Trim();
                if (value.Contains("search starts here:", StringComparison.Ordinal))
                {
                    isInSearch = true;
                }
                else if (value.StartsWith("End of search list.", StringComparison.Ordinal))
                {
                    isInSearch = false;
                }
                else if (isInSearch)
                {
                    bool isFramework = value.EndsWith(" (framework directory)", StringComparison.Ordinal);
                    string path = value.Replace(" (framework directory)", "", StringComparison.Ordinal);
                    if (ExistingDirectory(path) is string normalized)
                    {
                        includes.Add(new CompilerInclude(normalized,
                            isFramework ? ResourcePurpose.Framework : language == "c" ? ResourcePurpose.CInclude : ResourcePurpose.CppInclude));
                    }
                }
            }

            if (includes.Count == startCount)
            {
                diagnostics.Add(Missing($"No {language} header search paths were reported.", Path));
            }
        }

        ProcessResult? searchDirectories = await RequiredProbeAsync("-print-search-dirs").ConfigureAwait(false);
        string? libraries = searchDirectories?.StandardOutput.Split('\n')
            .FirstOrDefault(line => line.StartsWith("libraries:", StringComparison.Ordinal));
        string[] directories = libraries is null ? [] : libraries["libraries:".Length..].Trim().TrimStart('=')
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => ExistingDirectory(path)).OfType<string>()
            .Distinct(SearchPaths.Comparer).ToArray();

        string? resource = IsClang
            ? (await RequiredProbeAsync("-print-resource-dir").ConfigureAwait(false))?.StandardOutput.Trim()
            : (await RequiredProbeAsync("-print-file-name=include").ConfigureAwait(false))?.StandardOutput.Trim();
        resource = ExistingDirectory(resource);

        return new CompilerTargets(
            [new CompilerTarget(this, target, arguments.ToArray(), multilib, multiarch, sysroot,
                resource, includes, directories, diagnostics)], []);
    }

    internal Task<ProcessResult?> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        _context.ProbeAsync(
            _probePath,
            arguments,
            cancellationToken);

    internal Task<ProbeOutcome> RunDetailedAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        _context.ProbeDetailedAsync(_probePath, arguments, cancellationToken);

    internal static string NullInput => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    private static bool IsSimpleDirectory(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.IndexOfAny(['/', '\\', '\r', '\n']) < 0 && value is not "." and not "..";

    private static TargetArchitecture MacroArchitecture(string? macros)
    {
        string value = macros ?? "";
        return value.Contains("#define __x86_64__", StringComparison.Ordinal) ? TargetArchitecture.X64
            : value.Contains("#define __i386__", StringComparison.Ordinal) ? TargetArchitecture.X86
            : value.Contains("#define __aarch64__", StringComparison.Ordinal) ? TargetArchitecture.ARM64
            : value.Contains("#define __arm__", StringComparison.Ordinal) ? TargetArchitecture.ARM
            : value.Contains("#define __wasm32__", StringComparison.Ordinal) ? TargetArchitecture.Wasm32
            : TargetArchitecture.Unknown;
    }

    internal static Diagnostic Missing(string message, string path) =>
        new(DiagnosticSeverity.Warning, "target-probe", "Compiler target", message, path);
}

internal sealed record CompilerInclude(string Path, ResourcePurpose Purpose);

internal sealed record CompilerTargets(IReadOnlyList<CompilerTarget> Targets, IReadOnlyList<Diagnostic> Diagnostics);

internal sealed record CompilerTarget(
    CompilerProbe Compiler,
    TargetIdentity Identity,
    IReadOnlyList<string> Arguments,
    string? Multilib,
    string? Multiarch,
    string? Sysroot,
    string? ResourceDirectory,
    IReadOnlyList<CompilerInclude> Includes,
    IReadOnlyList<string> LibraryDirectories,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    internal async Task<CompilerFileResult> FindFileAsync(string name, CancellationToken cancellationToken)
    {
        ProbeOutcome outcome = await Compiler.RunDetailedAsync(
            Arguments.Append("-print-file-name=" + name).ToArray(), cancellationToken).ConfigureAwait(false);
        if (outcome.Status != ProbeStatus.Success)
        {
            return new CompilerFileResult(null, [CompilerProbe.Missing(outcome.Describe(), Compiler.Path)]);
        }

        string? path = outcome.Result?.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(path) || path == name)
        {
            return new CompilerFileResult(null, []);
        }

        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            return new CompilerFileResult(null,
                [CompilerProbe.Missing($"The driver reported a non-absolute file path: {path}", Compiler.Path)]);
        }

        try
        {
            string normalized = SearchPaths.Normalize(path);
            return File.Exists(normalized)
                ? new CompilerFileResult(normalized, [])
                : new CompilerFileResult(null,
                    [CompilerProbe.Missing($"The reported file does not exist after resolution: {normalized}", path)]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CompilerFileResult(null, [CompilerProbe.Missing(exception.Message, path)]);
        }
    }
}

internal sealed record CompilerFileResult(string? Path, IReadOnlyList<Diagnostic> Diagnostics);

internal sealed record CompilerOpenResult(
    CompilerProbe? Probe,
    ProbeOutcome? Failure = null,
    string? Error = null);
