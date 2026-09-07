namespace Incant.Core.Cpp.FindSdk;

/// <summary>Separates target-specific library paths from shared host search directories.</summary>
internal static class TargetResources
{
    internal static bool HasCompatibleDirectory(string path, TargetIdentity target)
    {
        foreach (string part in path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]))
        {
            if (part.Contains("-linux-", StringComparison.Ordinal) || part.Contains("-windows-", StringComparison.Ordinal)
                || part.Contains("-wasi", StringComparison.Ordinal) || part.Contains("-emscripten", StringComparison.Ordinal))
            {
                return target.HasSameAbi(new TargetIdentity(part));
            }
        }

        return false;
    }

    internal static bool HasConflictingDirectory(string path, TargetIdentity target)
    {
        foreach (string part in path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]))
        {
            if (part.Contains("-linux-", StringComparison.Ordinal) || part.Contains("-windows-", StringComparison.Ordinal)
                || part.Contains("-wasi", StringComparison.Ordinal) || part.Contains("-emscripten", StringComparison.Ordinal))
            {
                if (!target.HasSameAbi(new TargetIdentity(part)))
                {
                    return true;
                }
            }

            if (part == "lib64" && (target.Architecture != TargetArchitecture.X64 || target.Abi.EndsWith("x32", StringComparison.Ordinal))
                || part == "lib32" && target.Architecture != TargetArchitecture.X86
                || part == "libx32" && !target.Abi.EndsWith("x32", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasForeignLibc(CompilerTarget target)
    {
        if (target.Identity.Platform != TargetPlatform.Linux)
        {
            return false;
        }

        return target.Sysroot is null or "/" && target.Compiler.DefaultTarget is TargetIdentity defaultTarget && LibcFamily(defaultTarget) != LibcFamily(target.Identity)
            || SystemHeadersMatch(target.Sysroot ?? "/", target.Identity) is false;
    }

    internal static bool IsCompatibleFile(string path, CompilerTarget target, bool hasForeignLibc, bool isOwned = false)
    {
        bool? matchesBinary = BinaryImageReader.MatchesTarget(path, target.Identity);
        if (matchesBinary is false
            || HasConflictingDirectory(path, target.Identity) && !(isOwned && matchesBinary is true))
        {
            return false;
        }

        bool hasTargetDirectory = HasCompatibleDirectory(path, target.Identity);
        if (hasForeignLibc && !isOwned && !hasTargetDirectory)
        {
            return false;
        }

        return matchesBinary is true || hasTargetDirectory || isOwned
            || target.Compiler.DefaultTarget?.HasSameAbi(target.Identity) is true;
    }

    internal static bool? SystemHeadersMatch(string root, TargetIdentity target)
    {
        foreach (string suffix in new[] { "usr/include/features.h", "include/features.h" })
        {
            string path = Path.Combine(root, suffix);
            if (File.Exists(path))
            {
                string features = File.ReadAllText(path);
                if (System.Text.RegularExpressions.Regex.IsMatch(features, @"^\s*#\s*define\s+__GLIBC__\s+\d+",
                    System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                {
                    return LibcFamily(target) == "gnu";
                }
            }
        }

        foreach (string path in SearchPaths.Files(Path.Combine(root, "lib")))
        {
            if (Path.GetFileName(path).StartsWith("ld-musl-", StringComparison.Ordinal)
                && BinaryImageReader.Read(path).Contains(target.Architecture))
            {
                return LibcFamily(target) == "musl";
            }
        }

        return null;
    }

    private static string LibcFamily(TargetIdentity target) => target.Abi.StartsWith("gnu", StringComparison.Ordinal) ? "gnu"
        : target.Abi.StartsWith("musl", StringComparison.Ordinal) ? "musl" : target.Abi;

    internal static bool IsLibrary(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(".a", StringComparison.Ordinal) || name.EndsWith(".lib", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tbd", StringComparison.Ordinal) || (name.EndsWith(".so", StringComparison.Ordinal) || name.Contains(".so.", StringComparison.Ordinal))
            || name.EndsWith(".dylib", StringComparison.Ordinal);
    }

    internal static bool IsStartup(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(".o", StringComparison.Ordinal)
            && (name.StartsWith("crt", StringComparison.Ordinal) || name.StartsWith("Scrt", StringComparison.Ordinal)
                || name.StartsWith("rcrt", StringComparison.Ordinal) || name.StartsWith("clang_rt.crt", StringComparison.Ordinal));
    }

    internal static bool RuntimeMatches(string directory, string file, TargetIdentity target)
    {
        string directoryName = Path.GetFileName(directory).ToLowerInvariant();
        if (HasConflictingDirectory(directory, target) || BinaryImageReader.MatchesTarget(file, target) is false)
        {
            return false;
        }

        if (HasCompatibleDirectory(directory, target))
        {
            return true;
        }

        string name = Path.GetFileName(file).ToLowerInvariant();
        TargetPlatform directoryPlatform = directoryName switch
        {
            "windows" => TargetPlatform.Windows,
            "linux" => TargetPlatform.Linux,
            "darwin" => target.Platform,
            "wasi" => TargetPlatform.Wasi,
            _ => TargetPlatform.Unknown,
        };
        if (directoryPlatform != target.Platform)
        {
            return false;
        }

        if (directoryName == "darwin")
        {
            string suffix = target.Platform switch
            {
                TargetPlatform.MacOS => "osx",
                TargetPlatform.IOS => "ios",
                TargetPlatform.IOSSimulator => "iossim",
                TargetPlatform.TvOS => "tvos",
                TargetPlatform.TvOSSimulator => "tvossim",
                TargetPlatform.WatchOS => "watchos",
                TargetPlatform.WatchOSSimulator => "watchossim",
                TargetPlatform.VisionOS => "xros",
                TargetPlatform.VisionOSSimulator => "xrossim",
                _ => "",
            };
            return suffix.Length > 0 && (name.Contains("." + suffix + ".", StringComparison.Ordinal)
                || name.Contains("_" + suffix + ".", StringComparison.Ordinal))
                && BinaryImageReader.MatchesTarget(file, target) is true;
        }

        string[] names = target.Architecture switch
        {
            TargetArchitecture.X64 when target.Abi.EndsWith("x32", StringComparison.Ordinal) => ["x32"],
            TargetArchitecture.X64 => ["x86_64", "amd64"],
            TargetArchitecture.X86 => ["i386", "i686", "x86"],
            TargetArchitecture.ARM => ["arm", "armhf"],
            TargetArchitecture.ARM64 => ["aarch64", "arm64"],
            TargetArchitecture.Wasm32 => ["wasm32"],
            _ => [],
        };
        return names.Any(architecture => name.Contains("-" + architecture + ".", StringComparison.Ordinal)
            || name.Contains("-" + architecture + "-", StringComparison.Ordinal));
    }
}
