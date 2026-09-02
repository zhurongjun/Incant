using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Incant.Base;

namespace Incant.Core.Toolchains;

internal static partial class ProviderUtilities
{
    internal static TargetArchitecture GetHostArchitecture() => Platform.Arch switch
    {
        PlatformArch.X86 => TargetArchitecture.X86,
        PlatformArch.X64 => TargetArchitecture.X64,
        PlatformArch.ARM64 => TargetArchitecture.ARM64,
        _ => TargetArchitecture.Unknown,
    };

    internal static string GetExecutableName(string name) =>
        OperatingSystem.IsWindows() ? name + ".exe" : name;

    internal static IReadOnlyList<string> GetPathDirectories(DiscoveryContext context)
    {
        string? path = context.GetEnvironmentVariable("PATH");
        return string.IsNullOrWhiteSpace(path)
            ? []
            : path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => directory.Trim('"'))
                .Where(directory => directory.Length > 0)
                .ToArray();
    }

    internal static void AddCandidate(
        ICollection<Candidate> candidates,
        ISet<string> seenPaths,
        string? path,
        Source source)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = NormalizePath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (seenPaths.Add(fullPath))
        {
            candidates.Add(new Candidate(fullPath, source));
        }
        else
        {
            candidates.FirstOrDefault(candidate =>
                GetPathComparer().Equals(candidate.Path, fullPath))?.AddSource(source);
        }
    }

    internal static IEnumerable<string> EnumerateFiles(string directory, Func<string, bool> predicate)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(directory).Where(predicate).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static async Task<ProcessResult?> TryRunProbeAsync(
        string executable,
        IReadOnlyList<string> arguments,
        DiscoveryContext context,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        try
        {
            ProcessResult result = await Misc.RunProcessAsync(
                executable,
                arguments,
                new ProcessOptions
                {
                    Environment = CreateProbeEnvironment(context.Environment, environment),
                    Timeout = context.Options.ProbeTimeout,
                    EnsureUnixExecutablePermission = false,
                },
                cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? result : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or Win32Exception)
        {
            return null;
        }
    }

    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match match = VersionPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        string value = match.Value;
        return Version.TryParse(value, out Version? version) ? version : null;
    }

    internal static TargetArchitecture ParseArchitecture(string? targetTriple)
    {
        if (string.IsNullOrWhiteSpace(targetTriple))
        {
            return TargetArchitecture.Unknown;
        }

        string architecture = targetTriple.Split('-', 2)[0];
        return architecture.ToLowerInvariant() switch
        {
            "i386" or "i486" or "i586" or "i686" or "x86" => TargetArchitecture.X86,
            "amd64" or "x86_64" => TargetArchitecture.X64,
            "arm" or "armv7" or "armv7a" => TargetArchitecture.ARM,
            "aarch64" or "arm64" => TargetArchitecture.ARM64,
            "wasm32" => TargetArchitecture.Wasm32,
            _ => TargetArchitecture.Unknown,
        };
    }

    internal static Channel GetChannel(string path, string? versionText = null)
    {
        string combined = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) + " " + versionText;
        if (PreviewChannelPattern().IsMatch(combined))
        {
            return Channel.Preview;
        }

        if (ExperimentalChannelPattern().IsMatch(combined))
        {
            return Channel.Experimental;
        }

        return Channel.Stable;
    }

    internal static string? FindSiblingExecutable(string compilerPath, params string[] names)
    {
        string? directory = Path.GetDirectoryName(compilerPath);
        if (directory is null)
        {
            return null;
        }

        foreach (string name in names)
        {
            string path = Path.Combine(directory, GetExecutableName(name));
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    internal static bool TryReadProperties(string path, [NotNullWhen(true)] out Dictionary<string, string>? values)
    {
        values = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines(path))
            {
                int separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                string key = line[..separatorIndex].Trim();
                string value = line[(separatorIndex + 1)..].Trim();
                result[key] = value;
            }

            values = result;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string? FindExecutableOnPath(
        DiscoveryContext context,
        params string[] names)
    {
        foreach (string directory in GetPathDirectories(context))
        {
            foreach (string name in names)
            {
                string path = Path.Combine(directory, GetExecutableName(name));
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    internal static IReadOnlyDictionary<string, string?> CaptureEnvironment(
        IReadOnlyDictionary<string, string?>? replacement)
    {
        if (replacement is not null)
        {
            return new ReadOnlyDictionary<string, string?>(
                new Dictionary<string, string?>(replacement, GetEnvironmentComparer()));
        }

        var values = new Dictionary<string, string?>(GetEnvironmentComparer());
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name)
            {
                values[name] = entry.Value?.ToString();
            }
        }

        return new ReadOnlyDictionary<string, string?>(values);
    }

    internal static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string NormalizePath(string path)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        string relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath == ".")
        {
            return fullPath;
        }

        string resolvedPath = root;
        foreach (string segment in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(resolvedPath, segment);
            FileSystemInfo? fileSystemInfo = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate)
                    ? new FileInfo(candidate)
                    : null;
            if (fileSystemInfo is not null)
            {
                try
                {
                    FileSystemInfo? target = fileSystemInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is not null)
                    {
                        candidate = target.FullName;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException
                    or UnauthorizedAccessException
                    or PlatformNotSupportedException)
                {
                }
            }

            resolvedPath = candidate;
        }

        return Path.TrimEndingDirectorySeparator(resolvedPath);
    }

    private static IReadOnlyDictionary<string, string?> CreateProbeEnvironment(
        IReadOnlyDictionary<string, string?> capturedEnvironment,
        IReadOnlyDictionary<string, string?>? overrides)
    {
        StringComparer comparer = GetEnvironmentComparer();
        var values = new Dictionary<string, string?>(comparer);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && !capturedEnvironment.ContainsKey(name))
            {
                values[name] = null;
            }
        }

        foreach ((string name, string? value) in capturedEnvironment)
        {
            values[name] = value;
        }

        if (overrides is not null)
        {
            foreach ((string name, string? value) in overrides)
            {
                values[name] = value;
            }
        }

        return values;
    }

    private static StringComparer GetEnvironmentComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex(@"\d+(?:\.\d+){0,3}", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(
        @"(?:^|[^A-Za-z0-9])(?:preview|beta|pre|rc)(?:[._-]?\d+)?(?:[^A-Za-z0-9]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PreviewChannelPattern();

    [GeneratedRegex(
        @"(?:^|[^A-Za-z0-9])(?:nightly|dev)(?:[._-]?\d+)?(?:[^A-Za-z0-9]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExperimentalChannelPattern();
}
