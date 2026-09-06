using System.Text.RegularExpressions;

namespace Incant.Core.Cpp;

internal enum BundleKind
{
    AndroidNdk,
    Emscripten,
    WasiSdk,
}

internal sealed record BundleInstallation(
    BundleKind Kind, Candidate Candidate, string Root, string Bin, string? Compiler,
    string Sysroot, Version? Version, Channel Channel, string? TargetTriple = null);

internal sealed record BundleDiscoveryResult(
    IReadOnlyList<BundleInstallation> Installations, IReadOnlyList<Diagnostic> Diagnostics);

internal static partial class BundleLocator
{
    internal static async Task<BundleDiscoveryResult> FindAsync(
        BundleKind kind, string? explicitRoot, DiscoveryContext context, CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        var diagnostics = new List<Diagnostic>();
        void Add(string? path, Source source)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (Path.IsPathFullyQualified(path))
                {
                    candidates.Add(new Candidate(path, source));
                }
                else
                {
                    diagnostics.Add(Failure(kind, "A bundle discovery path must be absolute.", path));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                diagnostics.Add(Failure(kind, exception.Message, path));
            }
        }

        if (explicitRoot is not null)
        {
            Add(explicitRoot, Source.Explicit);
        }
        else
        {
            string[] variables = kind switch
            {
                BundleKind.AndroidNdk => ["ANDROID_NDK_ROOT", "ANDROID_NDK_HOME", "NDK_ROOT", "ANDROID_HOME", "ANDROID_SDK_ROOT"],
                BundleKind.Emscripten => ["EMSDK"],
                _ => ["WASI_SDK_PATH"],
            };
            foreach (string name in variables)
            {
                Add(context.GetEnvironmentVariable(name), Source.Environment);
            }

            string? configPath = context.GetEnvironmentVariable("EM_CONFIG");
            if (kind == BundleKind.Emscripten && !string.IsNullOrWhiteSpace(configPath))
            {
                try
                {
                    string config = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
                    Match match = EmscriptenRoot().Match(config);
                    if (match.Success)
                    {
                        Add(match.Groups[1].Value, Source.Environment);
                    }
                    else
                    {
                        diagnostics.Add(Failure(kind, "EM_CONFIG does not contain a literal EMSCRIPTEN_ROOT assignment.", configPath));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    diagnostics.Add(Failure(kind, exception.Message, configPath));
                }
            }

            string? home = context.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                string? standard = kind switch
                {
                    BundleKind.AndroidNdk when OperatingSystem.IsMacOS() => Path.Combine(home, "Library", "Android", "sdk"),
                    BundleKind.AndroidNdk when OperatingSystem.IsWindows() => Path.Combine(
                        context.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.Combine(home, "AppData", "Local"), "Android", "Sdk"),
                    BundleKind.AndroidNdk => Path.Combine(home, "Android", "Sdk"),
                    BundleKind.Emscripten => Path.Combine(home, "emsdk"),
                    _ => null,
                };
                Add(standard, Source.StandardPath);
            }

            if (kind == BundleKind.WasiSdk && !OperatingSystem.IsWindows())
            {
                Add("/opt/wasi-sdk", Source.StandardPath);
            }

            foreach (string directory in SearchPaths.PathDirectories(context))
            {
                string? executable = SearchPaths.Executable(directory, kind == BundleKind.Emscripten ? "emcc" : "clang",
                    wrappers: kind == BundleKind.Emscripten);
                Add(executable, Source.Path);
            }
        }

        BundleDiscoveryResult[] results = await Task.WhenAll(
            Candidate.Merge(candidates).Select(candidate =>
                InspectCandidateAsync(
                    kind, candidate, context, cancellationToken))).ConfigureAwait(false);
        BundleInstallation[] installations = results.SelectMany(result => result.Installations)
            .GroupBy(installation => installation.Root, SearchPaths.Comparer)
            .Select(group =>
            {
                BundleInstallation preferred = group.OrderBy(installation => installation.Candidate.Sources.Min()).First();
                Candidate merged = Candidate.Merge(group.SelectMany(installation => installation.Candidate.Sources
                    .Select(source => new Candidate(installation.Root, source, channel: installation.Channel)))).Single();
                return preferred with { Candidate = merged };
            }).OrderBy(installation => installation.Candidate.Sources.Min())
            .ThenBy(installation => installation.Root, SearchPaths.Comparer).ToArray();
        return new BundleDiscoveryResult(installations,
            diagnostics.Concat(results.SelectMany(result => result.Diagnostics)).ToArray());
    }

    private static async Task<BundleDiscoveryResult> InspectCandidateAsync(
        BundleKind kind, Candidate candidate, DiscoveryContext context, CancellationToken cancellationToken)
    {
        try
        {
            string[] roots = ExpandRoots(kind, candidate.Path).Distinct(SearchPaths.Comparer).ToArray();
            BundleDiscoveryResult[] results = await Task.WhenAll(
                roots.Select(root => InspectRootAsync(
                    kind, root, candidate, context, cancellationToken))).ConfigureAwait(false);
            return new BundleDiscoveryResult(results.SelectMany(result => result.Installations).ToArray(),
                results.SelectMany(result => result.Diagnostics).ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new BundleDiscoveryResult([], [Failure(kind, exception.Message, candidate.Path)]);
        }
    }

    private static async Task<BundleDiscoveryResult> InspectRootAsync(
        BundleKind kind, string root, Candidate candidate, DiscoveryContext context, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                return new BundleDiscoveryResult([], []);
            }

            string? revision;
            string bin;
            string sysroot;

            if (kind == BundleKind.AndroidNdk)
            {
                string? properties = await SearchPaths.ReadTextAsync(Path.Combine(root, "source.properties"), cancellationToken).ConfigureAwait(false);
                revision = properties?.Split('\n').FirstOrDefault(line => line.TrimStart().StartsWith("Pkg.Revision", StringComparison.Ordinal))?
                    .Split('=', 2).Last().Trim();
                if (revision is null || !(Directory.Exists(Path.Combine(root, "toolchains"))
                    || properties!.Contains("Android NDK", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(root).Contains("ndk", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(Path.GetDirectoryName(root)) == "ndk"))
                {
                    return new BundleDiscoveryResult([], []);
                }

                string? prebuilt = FindNdkPrebuilt(root, context);
                bin = Path.Combine(prebuilt ?? Path.Combine(root, "toolchains", "llvm", "prebuilt"), "bin");
                sysroot = Path.Combine(prebuilt ?? Path.Combine(root, "toolchains", "llvm", "prebuilt"), "sysroot");
            }
            else
            {
                bin = kind == BundleKind.Emscripten ? root : Path.Combine(root, "bin");
                sysroot = kind == BundleKind.Emscripten ? Path.Combine(root, "cache", "sysroot") : Path.Combine(root, "share", "wasi-sysroot");
                revision = await SearchPaths.ReadTextAsync(Path.Combine(root,
                    kind == BundleKind.Emscripten ? "emscripten-version.txt" : "VERSION"), cancellationToken).ConfigureAwait(false)
                    ?? await SearchPaths.ReadTextAsync(Path.Combine(root, "VERSION.txt"), cancellationToken).ConfigureAwait(false);
                bool hasIdentity = kind == BundleKind.Emscripten
                    ? File.Exists(Path.Combine(root, "emscripten-version.txt")) || File.Exists(Path.Combine(root, "emcc.py"))
                    : Directory.Exists(sysroot) || revision is not null && Path.GetFileName(root).Contains("wasi", StringComparison.OrdinalIgnoreCase);
                if (!hasIdentity && kind == BundleKind.WasiSdk && revision is not null)
                {
                    string? driver = SearchPaths.Executable(bin, "clang");
                    Base.ProcessResult? result = driver is null ? null
                        : await context.ProbeAsync(driver, ["-dumpmachine"], cancellationToken).ConfigureAwait(false);
                    hasIdentity = result?.StandardOutput.Contains("wasi", StringComparison.OrdinalIgnoreCase) == true;
                }

                if (!hasIdentity)
                {
                    return new BundleDiscoveryResult([], []);
                }
            }

            string? compiler = SearchPaths.Executable(bin, kind == BundleKind.Emscripten ? "emcc" : "clang",
                wrappers: kind == BundleKind.Emscripten);
            Version? version = SearchPaths.Version(revision);
            string? targetTriple = kind == BundleKind.WasiSdk
                ? await WasiTargetResolver.ResolveAsync(root, compiler, version, context, cancellationToken).ConfigureAwait(false)
                : null;
            Channel channel = revision is not null ? SearchPaths.Channel(revision) : SearchPaths.Channel(root);
            return new BundleDiscoveryResult([new BundleInstallation(kind, candidate, SearchPaths.Normalize(root), bin,
                compiler, sysroot, version, channel, targetTriple)], []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new BundleDiscoveryResult([], [Failure(kind, exception.Message, root)]);
        }
    }

    private static IEnumerable<string> ExpandRoots(BundleKind kind, string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return [];
        }

        string root = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        if (kind == BundleKind.Emscripten)
        {
            return [root, Path.Combine(root, "upstream", "emscripten")];
        }

        string? current = root;
        for (int depth = 0; depth < 7 && current is not null; depth++)
        {
            if (kind == BundleKind.AndroidNdk && File.Exists(Path.Combine(current, "source.properties"))
                || kind == BundleKind.WasiSdk && (Directory.Exists(Path.Combine(current, "share", "wasi-sysroot"))
                    || File.Exists(Path.Combine(current, "VERSION")) || File.Exists(Path.Combine(current, "VERSION.txt"))))
            {
                return [current];
            }

            current = Path.GetDirectoryName(current);
        }

        return kind == BundleKind.AndroidNdk
            ? SearchPaths.Directories(Path.Combine(root, "ndk")).Append(Path.Combine(root, "ndk-bundle"))
            : [root];
    }

    private static string? FindNdkPrebuilt(string root, DiscoveryContext context)
    {
        string[] names = context.HostOS switch
        {
            Base.PlatformOS.Windows => ["windows-x86_64"],
            Base.PlatformOS.OSX when context.HostArchitecture == TargetArchitecture.ARM64 => ["darwin-arm64", "darwin-x86_64"],
            Base.PlatformOS.OSX => ["darwin-x86_64"],
            Base.PlatformOS.Linux when context.HostArchitecture == TargetArchitecture.ARM64 => ["linux-aarch64", "linux-arm64"],
            Base.PlatformOS.Linux => ["linux-x86_64"],
            _ => [],
        };
        string directory = Path.Combine(root, "toolchains", "llvm", "prebuilt");
        return names.Select(name => Path.Combine(directory, name)).FirstOrDefault(Directory.Exists)
            ?? SearchPaths.Directories(directory)
                .Where(path => HostArchitecture(Path.GetFileName(path))
                    == context.HostArchitecture)
                .OrderBy(path => path, SearchPaths.Comparer)
                .FirstOrDefault();
    }

    private static TargetArchitecture HostArchitecture(string name) => name switch
    {
        "linux-aarch64" or "linux-arm64" or "darwin-arm64" or "windows-arm64" => TargetArchitecture.ARM64,
        "linux-x86_64" or "darwin-x86_64" or "windows-x86_64" => TargetArchitecture.X64,
        _ => TargetArchitecture.Unknown,
    };

    private static Diagnostic Failure(BundleKind kind, string message, string path) =>
        new(DiagnosticSeverity.Warning, "invalid-candidate", kind.ToString(), message, path);

    [GeneratedRegex("""^\s*EMSCRIPTEN_ROOT\s*=\s*['"]([^'"]+)['"]\s*(?:#.*)?$""", RegexOptions.Multiline)]
    private static partial Regex EmscriptenRoot();
}
