using System.Text.RegularExpressions;

namespace Incant.CXLegacy;

internal static partial class SearchPaths
{
    internal static string InvocationIdentity(string path)
    {
        try
        {
            return Normalize(path);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            return Incant.Internal.FileSystemPath.Absolute(path);
        }
    }

    internal static int SourcePriority(IReadOnlyList<Source> sources) =>
        sources.Count == 0 ? int.MaxValue : (int)sources.Min();

    internal static string PathKey(string path)
    {
        string normalized = Normalize(path);
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    internal static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string Normalize(string path) =>
        Incant.Internal.FileSystemPath.Resolve(path);

    internal static bool Contains(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative == "." || (!Path.IsPathRooted(relative)
            && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    internal static bool Related(string left, string right) => Contains(left, right) || Contains(right, left);

    internal static IEnumerable<string> Directories(string path) =>
        Directory.Exists(path) ? Directory.EnumerateDirectories(path) : [];

    internal static IEnumerable<string> Files(string path) =>
        Directory.Exists(path) ? Directory.EnumerateFiles(path) : [];

    internal static IEnumerable<string> PathDirectories(DiscoveryContext context) =>
        (context.GetEnvironmentVariable("PATH") ?? "").Split(
            Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.Trim('"')).Where(Path.IsPathFullyQualified);

    internal static string? Executable(string directory, string name, bool wrappers = false)
    {
        string[] suffixes = OperatingSystem.IsWindows()
            ? wrappers ? [".exe", ".bat", ".cmd", ".py", ""] : [".exe", ""]
            : wrappers ? ["", ".py"] : [""];
        foreach (string suffix in suffixes)
        {
            string path = Path.Combine(directory, name + suffix);
            try
            {
                if (File.Exists(Normalize(path)))
                {
                    return path;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An invalid link cannot supply this tool; other entry names remain eligible.
            }
        }

        return null;
    }

    internal static string? OnPath(DiscoveryContext context, string name) =>
        PathDirectories(context).Select(directory => Executable(directory, name)).FirstOrDefault(path => path is not null);

    internal static Version? Version(string? value)
    {
        Match match = VersionPattern().Match(value ?? "");
        if (!match.Success)
        {
            return null;
        }

        string text = match.Value.Contains('.') ? match.Value : match.Value + ".0";
        return System.Version.TryParse(text, out Version? version) ? version : null;
    }

    internal static Version? CompilerVersion(string text)
    {
        Match match = CompilerVersionPattern().Match(text);
        return match.Success ? Version(match.Groups[1].Value) : Version(text);
    }

    internal static TargetArchitecture Architecture(string? triple) => triple?.Split('-')[0].ToLowerInvariant() switch
    {
        "i386" or "i486" or "i586" or "i686" or "x86" => TargetArchitecture.X86,
        "x86_64" or "amd64" => TargetArchitecture.X64,
        "arm" or "armv7" or "armv7a" or "armv7k" => TargetArchitecture.ARM,
        "aarch64" or "arm64" or "arm64e" or "arm64_32" => TargetArchitecture.ARM64,
        "wasm32" => TargetArchitecture.Wasm32,
        _ => TargetArchitecture.Unknown,
    };

    internal static TargetPlatform Platform(string? triple)
    {
        string text = triple?.ToLowerInvariant() ?? "";
        if (text.Contains("android"))
        {
            return TargetPlatform.Android;
        }

        if (text.Contains("emscripten"))
        {
            return TargetPlatform.Emscripten;
        }

        if (text.Contains("wasi"))
        {
            return TargetPlatform.Wasi;
        }

        if (text.Contains("windows") && text.Contains("msvc"))
        {
            return TargetPlatform.Windows;
        }

        if (text.Contains("darwin") || text.Contains("macos"))
        {
            return TargetPlatform.MacOS;
        }

        if (text.Contains("ios"))
        {
            return text.Contains("simulator") ? TargetPlatform.IOSSimulator : TargetPlatform.IOS;
        }

        if (text.Contains("tvos"))
        {
            return text.Contains("simulator") ? TargetPlatform.TvOSSimulator : TargetPlatform.TvOS;
        }

        if (text.Contains("watchos"))
        {
            return text.Contains("simulator") ? TargetPlatform.WatchOSSimulator : TargetPlatform.WatchOS;
        }

        if (text.Contains("xros"))
        {
            return text.Contains("simulator") ? TargetPlatform.VisionOSSimulator : TargetPlatform.VisionOS;
        }

        return text.Contains("linux") ? TargetPlatform.Linux : TargetPlatform.Unknown;
    }

    internal static Channel Channel(string text) => PreviewPattern().IsMatch(text)
        ? global::Incant.CXLegacy.Channel.Preview : DevelopmentPattern().IsMatch(text) ? global::Incant.CXLegacy.Channel.Experimental : global::Incant.CXLegacy.Channel.Stable;

    internal static IReadOnlyList<T> Freeze<T>(IEnumerable<T>? values) =>
        Array.AsReadOnly((values ?? []).ToArray());

    internal static async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;

    internal static async Task<Version?> ReadVersionAsync(string root, CancellationToken cancellationToken, params string[] names)
    {
        foreach (string name in names)
        {
            string? text = await ReadTextAsync(Path.Combine(root, name), cancellationToken).ConfigureAwait(false);
            Version? version = Version(text);
            if (version is not null)
            {
                return version;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\d+(?:\.\d+){0,3}", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"clang(?:-cl)? version\s+(\d+(?:\.\d+){0,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompilerVersionPattern();

    [GeneratedRegex(@"(?:^|[^a-z0-9])(?:preview|beta|pre|rc)(?:[._-]?\d+)?(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PreviewPattern();

    [GeneratedRegex(@"(?:^|[^a-z0-9])(?:nightly|dev)(?:[._-]?\d+)?(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DevelopmentPattern();
}
