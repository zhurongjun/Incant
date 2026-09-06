using System.Text.Json;
using Microsoft.Win32;

namespace Incant.Core.Cpp;

internal static class WindowsLocator
{
    internal static async Task<IReadOnlyList<Candidate>> VisualStudiosAsync(
        string? explicitRoot, DiscoveryContext context, CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        if (!OperatingSystem.IsWindows())
        {
            return candidates;
        }

        if (explicitRoot is not null)
        {
            IReadOnlyList<Candidate> installed = await VisualStudiosAsync(null, context, cancellationToken).ConfigureAwait(false);
            string normalized = SearchPaths.Normalize(explicitRoot);
            Candidate? owner = installed.Where(candidate => SearchPaths.Contains(candidate.Path, normalized))
                .OrderByDescending(candidate => candidate.Path.Length).FirstOrDefault();
            candidates.Add(new Candidate(explicitRoot, Source.Explicit, owner?.ProductVersion, owner?.Channel));
            return candidates;
        }

        foreach (string name in new[] { "VSINSTALLDIR", "VCToolsInstallDir" })
        {
            string? path = context.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(path))
            {
                candidates.Add(new Candidate(path, Source.Environment,
                    SearchPaths.Version(context.GetEnvironmentVariable("VisualStudioVersion"))));
            }
        }

        string? programFilesX86 = context.GetEnvironmentVariable("ProgramFiles(x86)");
        string? vswhere = programFilesX86 is null ? null
            : SearchPaths.Executable(Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer"), "vswhere");
        if (vswhere is not null)
        {
            Base.ProcessResult? result = await context.ProbeAsync(vswhere,
                ["-all", "-prerelease", "-products", "*", "-format", "json", "-utf8"],
                cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
                foreach (JsonElement element in document.RootElement.EnumerateArray())
                {
                    string? path = element.GetProperty("installationPath").GetString();
                    if (path is not null)
                    {
                        Version? version = element.TryGetProperty("installationVersion", out JsonElement value)
                            ? SearchPaths.Version(value.GetString()) : null;
                        Channel? channel = element.TryGetProperty("isPrerelease", out JsonElement preview)
                            && preview.ValueKind is JsonValueKind.True or JsonValueKind.False
                            ? preview.GetBoolean() ? Channel.Preview : Channel.Stable : null;
                        candidates.Add(new Candidate(path, Source.Vendor, version, channel));
                    }
                }
            }
        }

        foreach (string name in new[] { "ProgramFiles", "ProgramFiles(x86)" })
        {
            string? directory = context.GetEnvironmentVariable(name);
            if (directory is null)
            {
                continue;
            }

            foreach (string versionDirectory in SearchPaths.Directories(Path.Combine(directory, "Microsoft Visual Studio")))
            {
                Version? version = Path.GetFileName(versionDirectory) switch
                {
                    "2017" => new Version(15, 0),
                    "2019" => new Version(16, 0),
                    "2022" => new Version(17, 0),
                    "2026" => new Version(18, 0),
                    _ => SearchPaths.Version(Path.GetFileName(versionDirectory)),
                };
                candidates.AddRange(SearchPaths.Directories(versionDirectory)
                    .Select(path => new Candidate(path, Source.StandardPath, version)));
            }
        }

        IReadOnlyList<Candidate> merged = Candidate.Merge(candidates);
        return merged.Select(candidate =>
        {
            Candidate? owner = merged.Where(item => item.Sources.Contains(Source.Vendor)
                && SearchPaths.Contains(item.Path, candidate.Path)).OrderByDescending(item => item.Path.Length).FirstOrDefault();
            return Candidate.Merge(candidate.Sources.Select(source => new Candidate(candidate.Path, source,
                owner?.ProductVersion ?? candidate.ProductVersion, owner?.Channel ?? candidate.Channel))).Single();
        }).ToArray();
    }

    internal static IEnumerable<string> MsvcRoots(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return [];
        }

        string current = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        for (int depth = 0; depth < 7; ++depth)
        {
            if (SearchPaths.Directories(Path.Combine(current, "bin")).Any(directory => Path.GetFileName(directory).StartsWith("Host", StringComparison.OrdinalIgnoreCase))
                || File.Exists(Path.Combine(current, "include", "vcruntime.h"))
                || string.Equals(Path.GetFileName(Path.GetDirectoryName(current)), "MSVC", StringComparison.OrdinalIgnoreCase)
                && System.Version.TryParse(Path.GetFileName(current), out _))
            {
                return [current];
            }

            string root = Path.Combine(current, "VC", "Tools", "MSVC");
            if (Directory.Exists(root))
            {
                return SearchPaths.Directories(root).Where(directory =>
                    System.Version.TryParse(Path.GetFileName(directory), out _)).ToArray();
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                break;
            }

            current = parent;
        }

        return [];
    }

    internal static string? MsvcRootForCompiler(string compilerPath)
    {
        if (!File.Exists(compilerPath)
            || !string.Equals(
                Path.GetFileNameWithoutExtension(compilerPath),
                "cl",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string compiler = SearchPaths.Normalize(compilerPath);
        return MsvcRoots(compiler)
            .Select(SearchPaths.Normalize)
            .Where(root => SearchPaths.Contains(Path.Combine(root, "bin"), compiler))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
    }

    internal static string MsvcEnvironment(string root)
    {
        string? parent = Path.GetDirectoryName(root);
        if (string.Equals(Path.GetFileName(parent), "MSVC", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(parent)), "Tools", StringComparison.OrdinalIgnoreCase))
        {
            string? vc = Path.GetDirectoryName(Path.GetDirectoryName(parent));
            if (string.Equals(Path.GetFileName(vc), "VC", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(vc)!;
            }
        }

        return root;
    }

    internal static IEnumerable<(string Name, TargetArchitecture Architecture)> Architectures =>
        [("x64", TargetArchitecture.X64), ("arm64", TargetArchitecture.ARM64), ("x86", TargetArchitecture.X86)];

    internal static IEnumerable<(string Name, TargetArchitecture Architecture)> RunnableHosts(DiscoveryContext context) =>
        Architectures.Where(item => item.Architecture == context.HostArchitecture
            || context.HostArchitecture == TargetArchitecture.X64 && item.Architecture == TargetArchitecture.X86
            || context.HostArchitecture == TargetArchitecture.ARM64)
            .OrderBy(item => item.Architecture == context.HostArchitecture ? 0 : 1);

    internal static IReadOnlyList<Candidate> WindowsKits(string? explicitRoot, DiscoveryContext context)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        if (explicitRoot is not null)
        {
            return [new Candidate(explicitRoot, Source.Explicit)];
        }

        var candidates = new List<Candidate>();
        string? environment = context.GetEnvironmentVariable("WindowsSdkDir");
        if (!string.IsNullOrWhiteSpace(environment))
        {
            candidates.Add(new Candidate(environment, Source.Environment));
        }

        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
                if (key?.GetValue("KitsRoot10") is string root)
                {
                    candidates.Add(new Candidate(root, Source.Vendor));
                }
            }
        }

        foreach (string name in new[] { "ProgramFiles(x86)", "ProgramFiles" })
        {
            if (context.GetEnvironmentVariable(name) is string directory)
            {
                string root = Path.Combine(directory, "Windows Kits", "10");
                if (Directory.Exists(root))
                {
                    candidates.Add(new Candidate(root, Source.StandardPath));
                }
            }
        }

        return Candidate.Merge(candidates);
    }
}
