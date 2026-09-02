using Incant.Base;

namespace Incant.Core.Toolchains;

/// <summary>Discovers Android NDK installations.</summary>
public sealed class AndroidNdkDiscoveryProvider : IDiscoveryProvider
{
    private static readonly IReadOnlyCollection<Kind> s_kinds = Array.AsReadOnly(
        [Kind.AndroidNdk]);

    /// <inheritdoc />
    public string Name => "Android NDK";

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
        foreach (PathHint hint in context.GetExplicitPaths(Kind.AndroidNdk))
        {
            AddRootCandidates(candidates, seenPaths, hint.Path, Source.Explicit);
        }

        foreach (string name in new[] { "ANDROID_NDK_HOME", "ANDROID_NDK_ROOT" })
        {
            AddRootCandidates(
                candidates,
                seenPaths,
                context.GetEnvironmentVariable(name),
                Source.Environment);
        }

        foreach (string name in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            AddRootCandidates(
                candidates,
                seenPaths,
                context.GetEnvironmentVariable(name),
                Source.Environment);
        }

        foreach (string path in GetStandardSdkRoots(context))
        {
            if (Directory.Exists(path))
            {
                AddRootCandidates(candidates, seenPaths, path, Source.StandardPath);
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
                    "The candidate does not contain a complete NDK LLVM prebuilt for this host.",
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
        string propertiesPath = Path.Combine(candidate.Path, "source.properties");
        if (!ProviderUtilities.TryReadProperties(propertiesPath, out Dictionary<string, string>? properties))
        {
            return (null, null);
        }

        string? prebuiltRoot = FindHostPrebuilt(
            candidate.Path,
            context.HostOS,
            context.HostArchitecture);
        if (prebuiltRoot is null)
        {
            return (null, null);
        }

        TargetArchitecture hostArchitecture = GetPrebuiltArchitecture(prebuiltRoot);
        string bin = Path.Combine(prebuiltRoot, "bin");
        string compiler = FindTool(bin, "clang");
        string cppCompiler = FindTool(bin, "clang++");
        string archiver = FindTool(bin, "llvm-ar");
        string linker = FindTool(bin, "ld.lld");
        string sysroot = Path.Combine(prebuiltRoot, "sysroot");
        if (!File.Exists(compiler)
            || !File.Exists(cppCompiler)
            || !File.Exists(archiver)
            || !File.Exists(linker)
            || !Directory.Exists(sysroot))
        {
            return (null, null);
        }

        ProcessResult? compilerVersionResult = await ProviderUtilities.TryRunProbeAsync(
            compiler,
            ["--version"],
            context,
            cancellationToken).ConfigureAwait(false);
        if (compilerVersionResult is null)
        {
            return (null, null);
        }

        Version? productVersion = properties.TryGetValue("Pkg.Revision", out string? revision)
            ? ProviderUtilities.ParseVersion(revision)
            : null;
        Version? compilerVersion = ProviderUtilities.ParseVersion(compilerVersionResult.StandardOutput);
        (IReadOnlyList<TargetArchitecture> architectures, IReadOnlyList<int> apiLevels) =
            InspectTargets(sysroot);
        if (architectures.Count == 0 || apiLevels.Count == 0)
        {
            return (null, null);
        }

        var components = new Component[]
        {
            new(ComponentKind.Compiler, compiler, hostArchitecture),
            new(ComponentKind.CppCompiler, cppCompiler, hostArchitecture),
            new(ComponentKind.Archiver, archiver, hostArchitecture),
            new(ComponentKind.Linker, linker, hostArchitecture),
            new(ComponentKind.Sysroot, sysroot, hostArchitecture),
        };
        var toolchain = new Installation(
            Kind.AndroidNdk,
            CompilerFamily.Clang,
            candidate.Path,
            context.HostOS,
            hostArchitecture,
            productVersion,
            compilerVersion,
            ProviderUtilities.GetChannel(candidate.Path, revision),
            candidate.Sources,
            [TargetPlatform.Android],
            architectures,
            components);
        var sdk = new SdkInstallation(
            Kind.AndroidNdk,
            TargetPlatform.Android,
            candidate.Path,
            sysroot,
            productVersion,
            candidate.Sources,
            architectures,
            apiLevels,
            [
                new Diagnostic(
                    DiagnosticSeverity.Info,
                    "android-api-levels",
                    "Android NDK",
                    $"Supported Android API levels: {string.Join(", ", apiLevels)}.",
                    sysroot),
            ]);
        return (toolchain, sdk);
    }

    private static (
        IReadOnlyList<TargetArchitecture> Architectures,
        IReadOnlyList<int> ApiLevels) InspectTargets(string sysroot)
    {
        string libraryRoot = Path.Combine(sysroot, "usr", "lib");
        var architectures = new List<TargetArchitecture>();
        var apiLevels = new HashSet<int>();
        foreach ((string triple, TargetArchitecture architecture) in GetAndroidAbis())
        {
            string targetRoot = Path.Combine(libraryRoot, triple);
            if (!Directory.Exists(targetRoot))
            {
                continue;
            }

            int[] targetApiLevels;
            try
            {
                targetApiLevels = Directory.EnumerateDirectories(targetRoot)
                    .Select(Path.GetFileName)
                    .Select(name => int.TryParse(name, out int apiLevel) ? apiLevel : -1)
                    .Where(apiLevel => apiLevel > 0)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (targetApiLevels.Length == 0)
            {
                continue;
            }

            architectures.Add(architecture);
            apiLevels.UnionWith(targetApiLevels);
        }

        return (architectures.AsReadOnly(), apiLevels.Order().ToArray());
    }

    private static IEnumerable<(string Triple, TargetArchitecture Architecture)> GetAndroidAbis()
    {
        yield return ("i686-linux-android", TargetArchitecture.X86);
        yield return ("x86_64-linux-android", TargetArchitecture.X64);
        yield return ("arm-linux-androideabi", TargetArchitecture.ARM);
        yield return ("aarch64-linux-android", TargetArchitecture.ARM64);
    }

    private static string? FindHostPrebuilt(
        string ndkRoot,
        PlatformOS hostOS,
        TargetArchitecture hostArchitecture)
    {
        string prebuiltRoot = Path.Combine(ndkRoot, "toolchains", "llvm", "prebuilt");
        if (!Directory.Exists(prebuiltRoot))
        {
            return null;
        }

        string[] preferredNames = hostOS switch
        {
            PlatformOS.Windows => ["windows-x86_64"],
            PlatformOS.OSX when hostArchitecture == TargetArchitecture.X64 =>
                ["darwin-x86_64"],
            PlatformOS.OSX => ["darwin-arm64", "darwin-x86_64"],
            PlatformOS.Linux => ["linux-x86_64"],
            _ => [],
        };
        foreach (string name in preferredNames)
        {
            string path = Path.Combine(prebuiltRoot, name);
            if (Directory.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static TargetArchitecture GetPrebuiltArchitecture(string prebuiltRoot)
    {
        string name = Path.GetFileName(prebuiltRoot);
        if (name.EndsWith("arm64", StringComparison.OrdinalIgnoreCase))
        {
            return TargetArchitecture.ARM64;
        }

        return name.EndsWith("x86_64", StringComparison.OrdinalIgnoreCase)
            ? TargetArchitecture.X64
            : TargetArchitecture.Unknown;
    }

    private static string FindTool(string directory, string name)
    {
        string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        return Path.Combine(directory, name + extension);
    }

    private static void AddRootCandidates(
        ICollection<Candidate> candidates,
        ISet<string> seenPaths,
        string? path,
        Source source)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!Directory.Exists(path))
        {
            ProviderUtilities.AddCandidate(candidates, seenPaths, path, source);
            return;
        }

        if (File.Exists(Path.Combine(path, "source.properties")))
        {
            ProviderUtilities.AddCandidate(candidates, seenPaths, path, source);
            return;
        }

        string ndkRoot = Path.Combine(path, "ndk");
        if (Directory.Exists(ndkRoot))
        {
            try
            {
                foreach (string ndk in Directory.EnumerateDirectories(ndkRoot))
                {
                    ProviderUtilities.AddCandidate(candidates, seenPaths, ndk, source);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        string legacyNdk = Path.Combine(path, "ndk-bundle");
        if (Directory.Exists(legacyNdk))
        {
            ProviderUtilities.AddCandidate(candidates, seenPaths, legacyNdk, source);
        }
    }

    private static IEnumerable<string> GetStandardSdkRoots(DiscoveryContext context)
    {
        string? home = context.GetEnvironmentVariable(
            OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            yield break;
        }

        if (OperatingSystem.IsWindows())
        {
            string? localAppData = context.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                yield return Path.Combine(localAppData, "Android", "Sdk");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Android", "sdk");
        }
        else
        {
            yield return Path.Combine(home, "Android", "Sdk");
        }
    }
}
