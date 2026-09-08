using System.Collections.ObjectModel;
using Incant.Base;

namespace Incant.CXLegacy;

internal static class AppleLocator
{
    internal static async Task<IReadOnlyList<Candidate>> EnvironmentsAsync(
        string? explicitRoot, DiscoveryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            return [];
        }

        if (explicitRoot is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(explicitRoot);
            if (!Path.IsPathFullyQualified(explicitRoot))
            {
                throw new ArgumentException("The developer path must be absolute.", nameof(explicitRoot));
            }

            string root = DeveloperDirectory(explicitRoot);
            return Directory.Exists(root) ? [new Candidate(root, Source.Explicit)] : [];
        }

        var candidates = new List<Candidate>();
        string? active = TryDeveloperDirectory(context.GetEnvironmentVariable("DEVELOPER_DIR"));
        if (active is not null)
        {
            candidates.Add(new Candidate(active, Source.Environment));
        }

        string? selected = await SelectedDeveloperAsync(context, cancellationToken).ConfigureAwait(false);
        if (selected is not null)
        {
            candidates.Add(new Candidate(selected, Source.Vendor));
        }

        foreach (string path in SearchPaths.Directories("/Applications").Where(path =>
            Path.GetFileName(path).StartsWith("Xcode", StringComparison.Ordinal) && path.EndsWith(".app", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? developer = TryDeveloperDirectory(path);
            if (developer is not null)
            {
                candidates.Add(new Candidate(developer, Source.StandardPath));
            }
        }

        string? commandLineTools = TryDeveloperDirectory("/Library/Developer/CommandLineTools");
        if (commandLineTools is not null)
        {
            candidates.Add(new Candidate(commandLineTools, Source.StandardPath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Candidate.Merge(candidates);
    }

    internal static string DeveloperDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string directory = Path.TrimEndingDirectorySeparator(path);
        return directory.EndsWith(".app", StringComparison.Ordinal)
            ? Path.Combine(directory, "Contents", "Developer") : directory;
    }

    internal static string? FindDeveloper(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The Apple installation path must be absolute.", nameof(path));
        }

        string resolved = SearchPaths.Normalize(path);
        string? current = File.Exists(resolved) ? Path.GetDirectoryName(resolved) : resolved;
        while (current is not null)
        {
            if (IsDeveloperDirectory(current))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    internal static async Task<string?> ResolveDeveloperAsync(DiscoveryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        string? active = TryDeveloperDirectory(context.GetEnvironmentVariable("DEVELOPER_DIR"));
        return active ?? await SelectedDeveloperAsync(context, cancellationToken).ConfigureAwait(false);
    }

    // Preserve the caller's identity separately from the executable used for probes and installation ownership.
    internal static async Task<CompilerResolution> ResolveCompilerAsync(
        string compilerPath, DiscoveryContext context, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compilerPath);
        ArgumentNullException.ThrowIfNull(context);
        if (!Path.IsPathFullyQualified(compilerPath))
        {
            throw new ArgumentException("The compiler path must be absolute.", nameof(compilerPath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(compilerPath))
        {
            return new CompilerResolution(compilerPath, null, null);
        }

        string resolved = SearchPaths.Normalize(compilerPath);
        string? developer = FindDeveloper(resolved);
        if (developer is not null || !OperatingSystem.IsMacOS() || !IsSystemWrapper(resolved))
        {
            return new CompilerResolution(compilerPath, resolved, developer);
        }

        developer = await ResolveDeveloperAsync(context, cancellationToken).ConfigureAwait(false);
        string toolName = Path.GetFileName(resolved) is "c++" or "clang++" ? "clang++" : "clang";
        ProcessResult? result = await context.ProbeAsync("/usr/bin/xcrun", ["--no-cache", "--find", toolName],
            cancellationToken, Environment(developer)).ConfigureAwait(false);
        string? reported = result?.StandardOutput.Trim();
        if (!string.IsNullOrWhiteSpace(reported) && Path.IsPathFullyQualified(reported) && File.Exists(reported))
        {
            string actual = SearchPaths.Normalize(reported);
            string? actualDeveloper = FindDeveloper(actual);
            if (!IsSystemWrapper(actual) && actualDeveloper is not null
                && (developer is null || SearchPaths.Comparer.Equals(developer, actualDeveloper)))
            {
                return new CompilerResolution(compilerPath, actual, actualDeveloper);
            }
        }

        return new CompilerResolution(compilerPath, null, developer);
    }

    internal static IReadOnlyDictionary<string, string?> Environment(string? developer) =>
        new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>
        {
            ["DEVELOPER_DIR"] = TryDeveloperDirectory(developer),
            // Clear redirects only in this probe's environment, including an invalid ambient developer path.
            ["SDKROOT"] = null,
            ["TOOLCHAINS"] = null,
        });

    private static async Task<string?> SelectedDeveloperAsync(DiscoveryContext context, CancellationToken cancellationToken)
    {
        ProcessResult? result = await context.ProbeAsync("/usr/bin/xcode-select", ["-p"],
            cancellationToken, Environment(null)).ConfigureAwait(false);
        return TryDeveloperDirectory(result?.StandardOutput.Trim());
    }

    private static string? TryDeveloperDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            string developer = SearchPaths.Normalize(DeveloperDirectory(path));
            return IsDeveloperDirectory(developer) ? developer : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unusable optional environment path does not prevent discovering other installations.
            return null;
        }
    }

    private static bool IsDeveloperDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        bool isXcode = Path.GetFileName(path) == "Developer" && Path.GetFileName(Path.GetDirectoryName(path)) == "Contents"
            && (Directory.Exists(Path.Combine(path, "Toolchains")) || Directory.Exists(Path.Combine(path, "Platforms")));
        bool isCommandLineTools = Path.GetFileName(path) == "CommandLineTools"
            && (Directory.Exists(Path.Combine(path, "usr")) || Directory.Exists(Path.Combine(path, "SDKs")));
        return isXcode || isCommandLineTools;
    }

    private static bool IsSystemWrapper(string path) => Path.GetDirectoryName(path) == "/usr/bin"
        && Path.GetFileName(path) is "cc" or "c++" or "clang" or "clang++";

    // A null resolved path means that discovery must not execute the missing or unresolved compiler wrapper.
    internal sealed record CompilerResolution(string CompilerPath, string? ResolvedCompilerPath, string? DeveloperPath);

    internal static string SdkName(TargetPlatform? platform) => platform switch
    {
        TargetPlatform.IOS => "iphoneos",
        TargetPlatform.IOSSimulator => "iphonesimulator",
        TargetPlatform.TvOS => "appletvos",
        TargetPlatform.TvOSSimulator => "appletvsimulator",
        TargetPlatform.WatchOS => "watchos",
        TargetPlatform.WatchOSSimulator => "watchsimulator",
        TargetPlatform.VisionOS => "xros",
        TargetPlatform.VisionOSSimulator => "xrsimulator",
        _ => "macosx",
    };

    internal static TargetPlatform Platform(string name) => name.ToLowerInvariant() switch
    {
        "macosx" => TargetPlatform.MacOS,
        "iphoneos" => TargetPlatform.IOS,
        "iphonesimulator" => TargetPlatform.IOSSimulator,
        "appletvos" => TargetPlatform.TvOS,
        "appletvsimulator" => TargetPlatform.TvOSSimulator,
        "watchos" => TargetPlatform.WatchOS,
        "watchsimulator" => TargetPlatform.WatchOSSimulator,
        "xros" => TargetPlatform.VisionOS,
        "xrsimulator" => TargetPlatform.VisionOSSimulator,
        _ => TargetPlatform.Unknown,
    };
}
