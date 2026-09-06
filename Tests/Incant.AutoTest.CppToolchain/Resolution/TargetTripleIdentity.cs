using System.Text.RegularExpressions;
using Incant.Core.Cpp;

namespace Incant.AutoTest.CppToolchain;

internal static partial class TargetTripleIdentity
{
    private const string LegacyWasiPreview1 = "wasm32-wasi";
    private const string WasiPreview1 = "wasm32-wasip1";

    internal static bool AreEquivalent(string? left, string? right) =>
        left is not null
        && right is not null
        && string.Equals(Canonicalize(left), Canonicalize(right), StringComparison.Ordinal);

    internal static bool Matches(
        string triple,
        TargetPlatform platform,
        TargetArchitecture architecture)
    {
        string value = Canonicalize(triple);
        bool architectureMatches = architecture switch
        {
            TargetArchitecture.X86 => value.StartsWith("i386", StringComparison.Ordinal),
            TargetArchitecture.X64 => value.StartsWith("x86_64", StringComparison.Ordinal),
            TargetArchitecture.ARM => value.StartsWith("arm", StringComparison.Ordinal),
            TargetArchitecture.ARM64 => value.StartsWith("aarch64", StringComparison.Ordinal),
            TargetArchitecture.Wasm32 => value.StartsWith("wasm32", StringComparison.Ordinal),
            _ => false,
        };
        bool platformMatches = platform switch
        {
            TargetPlatform.Windows => value.Contains("windows", StringComparison.Ordinal)
                || value.Contains("msvc", StringComparison.Ordinal),
            TargetPlatform.Linux => value.Contains("linux", StringComparison.Ordinal)
                && !value.Contains("android", StringComparison.Ordinal),
            TargetPlatform.MacOS => value.Contains("darwin", StringComparison.Ordinal)
                || value.Contains("macos", StringComparison.Ordinal),
            TargetPlatform.IOS => value.Contains("ios", StringComparison.Ordinal)
                && !value.Contains("simulator", StringComparison.Ordinal),
            TargetPlatform.IOSSimulator => value.Contains("ios", StringComparison.Ordinal)
                && value.Contains("simulator", StringComparison.Ordinal),
            TargetPlatform.Android => value.Contains("android", StringComparison.Ordinal),
            TargetPlatform.Emscripten => value.Contains("emscripten", StringComparison.Ordinal),
            TargetPlatform.Wasi => value.Contains("wasi", StringComparison.Ordinal),
            _ => false,
        };
        return architectureMatches && platformMatches;
    }

    internal static string Apple(
        TargetPlatform platform,
        TargetArchitecture architecture,
        Version? deploymentVersion)
    {
        string architectureName = architecture switch
        {
            TargetArchitecture.ARM64 => "arm64",
            TargetArchitecture.X64 => "x86_64",
            _ => throw new ArgumentOutOfRangeException(
                nameof(architecture), architecture, null),
        };
        string version = deploymentVersion is null
            ? string.Empty
            : deploymentVersion.ToString();
        string suffix = platform switch
        {
            TargetPlatform.MacOS => "macosx" + version,
            TargetPlatform.IOS => "ios" + version,
            TargetPlatform.IOSSimulator => "ios" + version + "-simulator",
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
        };
        return $"{architectureName}-apple-{suffix}";
    }

    internal static string Canonicalize(string triple)
    {
        string[] parts = triple.Trim().ToLowerInvariant().Split('-');
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        parts[0] = parts[0] switch
        {
            "i486" or "i586" or "i686" or "x86" => "i386",
            "amd64" => "x86_64",
            "arm64" => "aarch64",
            "armv7" or "armv7a" => "arm",
            _ => parts[0],
        };
        if (parts.Length > 2 && parts[1] is "pc" or "unknown" or "none")
        {
            parts = parts.Where((_, index) => index != 1).ToArray();
        }

        string canonical = string.Join('-', parts);
        if (parts[0] == "arm")
        {
            canonical = canonical.Replace(
                "-androideabi", "-android", StringComparison.Ordinal);
        }
        else if (canonical == LegacyWasiPreview1)
        {
            canonical = WasiPreview1;
        }
        else if (canonical.StartsWith(
            LegacyWasiPreview1 + "-", StringComparison.Ordinal))
        {
            canonical = WasiPreview1 + canonical[LegacyWasiPreview1.Length..];
        }

        return DeploymentPattern().Replace(canonical, match =>
        {
            string platform = match.Groups[1].Value == "macos"
                ? "macosx"
                : match.Groups[1].Value;
            string[] components = match.Groups[2].Value.Split('.');
            int count = components.Length;
            while (count > 1 && components[count - 1] == "0")
            {
                count--;
            }

            return platform + string.Join('.', components.Take(count));
        });
    }

    [GeneratedRegex(
        @"(?<=-)(macosx|macos|ios|tvos|watchos|xros|darwin)(\d+(?:\.\d+)*)(?=-|$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeploymentPattern();
}
