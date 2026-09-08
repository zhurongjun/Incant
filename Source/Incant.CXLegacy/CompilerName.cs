using System.Text.RegularExpressions;

namespace Incant.CXLegacy;

/// <summary>Parses invocation spelling without assigning a compiler family from its filename.</summary>
internal sealed partial record CompilerName(string Prefix, string Driver, string VersionSuffix)
{
    internal int DriverRank => Driver is "g++" or "c++" or "clang++" ? 2 : Driver == "clang-cl" ? 1 : 0;

    internal bool IsTargetQualified(TargetIdentity? target)
    {
        if (Prefix.Length == 0)
        {
            return true;
        }

        var prefixTarget = new TargetIdentity(Prefix.TrimEnd('-'));
        return target is not null
            && prefixTarget.Architecture != TargetArchitecture.Unknown
            && prefixTarget.Platform != TargetPlatform.Unknown
            && prefixTarget.HasSameAbi(target);
    }

    internal bool IsCompanionOf(CompilerName other) =>
        (Driver, other.Driver) is ("gcc", "g++") or ("g++", "gcc")
            or ("cc", "c++") or ("c++", "cc")
            or ("clang", "clang++") or ("clang++", "clang");

    internal static CompilerName? Parse(string path)
    {
        Match match = NamePattern().Match(ExecutableStem(path));
        return match.Success
            ? new CompilerName(match.Groups["prefix"].Value,
                match.Groups["driver"].Value.ToLowerInvariant(), match.Groups["suffix"].Value)
            : null;
    }

    internal static bool IsDriver(string name) => name is
        "clang-cl" or "clang++" or "clang" or "g++" or "gcc" or "c++" or "cc";

    internal static string ExecutableStem(string path)
    {
        string name = Path.GetFileName(path);
        string extension = Path.GetExtension(name);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".py", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(name) : name;
    }

    [GeneratedRegex(
        @"^(?<prefix>(?:[A-Za-z0-9_+.]+-)*?)(?<driver>clang-cl|clang\+\+|clang|gcc|g\+\+|cc|c\+\+)(?<suffix>-?\d+(?:\.\d+)*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
