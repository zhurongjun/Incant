using System.Text.RegularExpressions;

namespace Incant.CX;

/// <summary>Interprets target identity without treating a requested label as evidence of installed support.</summary>
internal sealed partial record TargetIdentity(string Triple)
{
    internal TargetArchitecture Architecture => SearchPaths.Architecture(Triple);

    internal TargetPlatform Platform => SearchPaths.Platform(Triple);

    internal string Canonical => Canonicalize(Triple);

    internal string Abi => AbiPattern().Match(Canonical).Value;

    internal static bool AreEquivalent(string? left, string? right) =>
        left is not null && right is not null && string.Equals(Canonicalize(left), Canonicalize(right), StringComparison.Ordinal);

    internal bool HasSameAbi(TargetIdentity other) =>
        Platform == other.Platform && Architecture == other.Architecture
        && string.Equals(Abi, other.Abi, StringComparison.Ordinal)
        && (Platform is not TargetPlatform.Unknown && Abi.Length > 0 || AreEquivalent(Triple, other.Triple));

    internal TargetIdentity WithArchitecture(TargetArchitecture architecture, bool isX32 = false)
    {
        string[] parts = Canonical.Split('-');
        parts[0] = Architecture == architecture ? parts[0] : architecture switch
        {
            TargetArchitecture.X86 => "i386",
            TargetArchitecture.X64 => "x86_64",
            TargetArchitecture.ARM => "arm",
            TargetArchitecture.ARM64 => "aarch64",
            TargetArchitecture.Wasm32 => "wasm32",
            _ => parts[0],
        };
        if (Platform == TargetPlatform.Linux)
        {
            for (int index = 1; index < parts.Length; index++)
            {
                if (parts[index] is "gnu" or "gnux32")
                {
                    parts[index] = isX32 ? "gnux32" : "gnu";
                }
                else if (parts[index] is "musl" or "muslx32")
                {
                    parts[index] = isX32 ? "muslx32" : "musl";
                }
            }
        }

        return new TargetIdentity(string.Join('-', parts));
    }

    private static string Canonicalize(string triple)
    {
        string[] parts = triple.Trim().ToLowerInvariant().Split('-');
        if (parts.Length == 0)
        {
            return "";
        }

        parts[0] = parts[0] switch
        {
            "i486" or "i586" or "i686" or "x86" => "i386",
            "amd64" => "x86_64",
            "arm64" => "aarch64",
            "armv7" or "armv7a" => "arm",
            _ => parts[0],
        };
        // Vendor spelling is not an ABI. OS, environment and deployment suffixes remain significant.
        if (parts.Length > 2 && parts[1] is "pc" or "unknown" or "none")
        {
            parts = parts.Where((_, index) => index != 1).ToArray();
        }

        string canonical = string.Join('-', parts);
        if (parts[0] == "arm")
        {
            canonical = canonical.Replace("-androideabi", "-android", StringComparison.Ordinal);
        }
        else if (canonical == WasiTargetResolver.LegacyPreview1Triple)
        {
            canonical = WasiTargetResolver.Preview1Triple;
        }
        else if (canonical.StartsWith(WasiTargetResolver.LegacyPreview1Triple + "-", StringComparison.Ordinal))
        {
            canonical = WasiTargetResolver.Preview1Triple + canonical[WasiTargetResolver.LegacyPreview1Triple.Length..];
        }

        return DeploymentPattern().Replace(canonical, match =>
        {
            string platform = match.Groups[1].Value == "macos" ? "macosx" : match.Groups[1].Value;
            string[] version = match.Groups[2].Value.Split('.');
            int count = version.Length;
            while (count > 1 && version[count - 1] == "0")
            {
                count--;
            }

            return platform + string.Join('.', version.Take(count));
        });
    }

    [GeneratedRegex(@"(?<=-)(macosx|macos|ios|tvos|watchos|xros|darwin)(\d+(?:\.\d+)*)(?=-|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DeploymentPattern();

    [GeneratedRegex(@"(?:musleabihf|musleabi|muslx32|musl|gnueabihf|gnueabi|gnux32|gnu|androideabi|android|msvc|wasi(?:p[123])?|emscripten)(?=-|\d|$)", RegexOptions.CultureInvariant)]
    private static partial Regex AbiPattern();
}
