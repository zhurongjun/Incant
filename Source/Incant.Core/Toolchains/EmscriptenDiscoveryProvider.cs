using Incant.Base;

namespace Incant.Core.Toolchains;

/// <summary>Discovers Emscripten SDK installations.</summary>
public sealed class EmscriptenDiscoveryProvider : IDiscoveryProvider
{
    private static readonly IReadOnlyCollection<Kind> s_kinds = Array.AsReadOnly(
        [Kind.Emscripten]);

    /// <inheritdoc />
    public string Name => "Emscripten";

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
        foreach (PathHint hint in context.GetExplicitPaths(Kind.Emscripten))
        {
            AddRootCandidate(candidates, seenPaths, hint.Path, Source.Explicit);
        }

        AddRootCandidate(
            candidates,
            seenPaths,
            context.GetEnvironmentVariable("EMSDK"),
            Source.Environment);
        AddConfigCandidate(candidates, seenPaths, context.GetEnvironmentVariable("EM_CONFIG"));
        string? home = context.GetEnvironmentVariable(
            OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            string standardRoot = Path.Combine(home, "emsdk");
            if (Directory.Exists(standardRoot))
            {
                AddRootCandidate(
                    candidates,
                    seenPaths,
                    standardRoot,
                    Source.StandardPath);
            }
        }

        foreach (string directory in ProviderUtilities.GetPathDirectories(context))
        {
            foreach (string name in GetWrapperNames("emcc"))
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path))
                {
                    AddRootCandidate(candidates, seenPaths, directory, Source.Path);
                }
            }
        }

        var toolchains = new List<Installation>();
        var sdks = new List<SdkInstallation>();
        var diagnostics = new List<Diagnostic>();
        foreach (Candidate candidate in candidates)
        {
            (Installation? toolchain, SdkInstallation? sdk) = await InspectAsync(
                candidate,
                context,
                cancellationToken).ConfigureAwait(false);
            if (toolchain is null || sdk is null)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "invalid-candidate",
                    Name,
                    "The candidate does not contain the required Emscripten compiler wrappers and archiver.",
                    candidate.Path));
                continue;
            }

            toolchains.Add(toolchain);
            sdks.Add(sdk);
        }

        return new DiscoveryResult(toolchains, sdks, diagnostics);
    }

    private static async Task<(Installation? Installation, SdkInstallation? Sdk)> InspectAsync(
        Candidate candidate,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        string? emscriptenRoot = FindEmscriptenRoot(candidate.Path);
        if (emscriptenRoot is null)
        {
            return (null, null);
        }

        string? compiler = FindWrapper(emscriptenRoot, "emcc");
        string? cppCompiler = FindWrapper(emscriptenRoot, "em++");
        string? archiver = FindWrapper(emscriptenRoot, "emar");
        string? ranlib = FindWrapper(emscriptenRoot, "emranlib");
        string? linker = FindWrapper(emscriptenRoot, "emcc");
        if (compiler is null
            || cppCompiler is null
            || archiver is null
            || ranlib is null
            || linker is null)
        {
            return (null, null);
        }

        Version? version = TryReadVersion(emscriptenRoot);
        string versionText = version?.ToString() ?? string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            ProcessResult? versionResult = await ProviderUtilities.TryRunProbeAsync(
                compiler,
                ["--version"],
                context,
                cancellationToken).ConfigureAwait(false);
            if (versionResult is null)
            {
                return (null, null);
            }

            versionText = versionResult.StandardOutput + versionResult.StandardError;
            version ??= ProviderUtilities.ParseVersion(versionText);
        }

        string cacheSysroot = Path.Combine(emscriptenRoot, "cache", "sysroot");
        string sysroot = Directory.Exists(cacheSysroot) ? cacheSysroot : emscriptenRoot;
        var components = new Component[]
        {
            new(ComponentKind.Compiler, compiler, context.HostArchitecture, TargetArchitecture.Wasm32),
            new(ComponentKind.CppCompiler, cppCompiler, context.HostArchitecture, TargetArchitecture.Wasm32),
            new(ComponentKind.Archiver, archiver, context.HostArchitecture, TargetArchitecture.Wasm32),
            new(ComponentKind.Ranlib, ranlib, context.HostArchitecture, TargetArchitecture.Wasm32),
            new(ComponentKind.Linker, linker, context.HostArchitecture, TargetArchitecture.Wasm32),
            new(ComponentKind.Sysroot, sysroot, context.HostArchitecture, TargetArchitecture.Wasm32),
        };
        var toolchain = new Installation(
            Kind.Emscripten,
            CompilerFamily.Clang,
            emscriptenRoot,
            context.HostOS,
            context.HostArchitecture,
            version,
            version,
            ProviderUtilities.GetChannel(candidate.Path, versionText),
            candidate.Sources,
            [TargetPlatform.Emscripten],
            [TargetArchitecture.Wasm32],
            components,
            "wasm32-unknown-emscripten");
        var sdk = new SdkInstallation(
            Kind.Emscripten,
            TargetPlatform.Emscripten,
            emscriptenRoot,
            sysroot,
            version,
            candidate.Sources,
            [TargetArchitecture.Wasm32]);
        return (toolchain, sdk);
    }

    private static string? FindEmscriptenRoot(string path)
    {
        foreach (string candidate in new[]
        {
            path,
            Path.Combine(path, "upstream", "emscripten"),
        })
        {
            if (FindWrapper(candidate, "emcc") is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindWrapper(string directory, string name)
    {
        foreach (string wrapperName in GetWrapperNames(name))
        {
            string path = Path.Combine(directory, wrapperName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetWrapperNames(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return name + ".bat";
            yield return name + ".cmd";
            yield return name + ".py";
        }

        yield return name;
    }

    private static Version? TryReadVersion(string emscriptenRoot)
    {
        foreach (string name in new[] { "emscripten-version.txt", "VERSION" })
        {
            string path = Path.Combine(emscriptenRoot, name);
            try
            {
                if (File.Exists(path))
                {
                    return ProviderUtilities.ParseVersion(File.ReadAllText(path));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    private static void AddConfigCandidate(
        ICollection<Candidate> candidates,
        ISet<string> seenPaths,
        string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return;
        }

        try
        {
            foreach (string line in File.ReadLines(configPath))
            {
                if (!line.TrimStart().StartsWith("EMSCRIPTEN_ROOT", StringComparison.Ordinal))
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator < 0)
                {
                    continue;
                }

                string path = line[(separator + 1)..].Trim().Trim('\'', '"');
                AddRootCandidate(candidates, seenPaths, path, Source.Environment);
                break;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void AddRootCandidate(
        ICollection<Candidate> candidates,
        ISet<string> seenPaths,
        string? path,
        Source source)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        ProviderUtilities.AddCandidate(candidates, seenPaths, normalized, source);
    }
}
