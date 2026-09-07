using System.Text.RegularExpressions;

namespace Incant.Core.Cpp;

/// <summary>Resolves the default WASI target shared by bundled tool and SDK discovery.</summary>
internal static partial class WasiTargetResolver
{
    internal const string LegacyPreview1Triple = "wasm32-wasi";
    internal const string Preview1Triple = "wasm32-wasip1";

    internal static async Task<string> ResolveAsync(
        string root,
        string? compiler,
        Version? version,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        if (compiler is not null)
        {
            Base.ProcessResult? result = await context.ProbeAsync(
                compiler, ["-dumpmachine"], cancellationToken).ConfigureAwait(false);
            string? target = ExtractTarget(result?.StandardOutput);
            if (target is not null)
            {
                return target;
            }
        }

        foreach (string relativePath in new[]
        {
            Path.Combine("share", "cmake", "wasi-sdk.cmake"),
            Path.Combine("share", "cmake", "wasi-sdk-p1.cmake"),
            Path.Combine("share", "cmake", "wasi-sdk-p2.cmake"),
        })
        {
            string? metadata = await SearchPaths.ReadTextAsync(
                Path.Combine(root, relativePath), cancellationToken).ConfigureAwait(false);
            string? target = ExtractTarget(metadata);
            if (target is not null)
            {
                return target;
            }
        }

        return version?.Major >= 33 ? Preview1Triple : LegacyPreview1Triple;
    }

    // These are ordered layout alternatives, not additive resource search paths.
    internal static IReadOnlyList<string> ResourceTripleCandidates(string targetTriple)
    {
        string canonical = new TargetIdentity(targetTriple).Canonical;
        string[] candidates = canonical == Preview1Triple
            ? [targetTriple, Preview1Triple, LegacyPreview1Triple]
            : [targetTriple];
        return candidates.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string? ExtractTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Match match = WasiTarget().Match(value);
        return match.Success ? new TargetIdentity(match.Groups["triple"].Value).Canonical : null;
    }

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])(?<triple>wasm32(?:-(?:unknown|none))?-(?:wasip[123]|wasi)(?:-[A-Za-z0-9_.]+)*)(?![A-Za-z0-9_])",
        RegexOptions.CultureInvariant)]
    private static partial Regex WasiTarget();
}
