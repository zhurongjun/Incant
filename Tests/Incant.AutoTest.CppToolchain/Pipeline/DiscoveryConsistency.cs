using Resource = Incant.Core.Cpp.FindSdk.Resource;
using Sdk = Incant.Core.Cpp.FindSdk.Sdk;
using TargetLayout = Incant.Core.Cpp.FindSdk.TargetLayout;
using ToolSet = Incant.Core.Cpp.FindTools.ToolSet;

namespace Incant.AutoTest.CppToolchain;

internal static class DiscoveryConsistency
{
    internal static void CompareToolSets(
        IReadOnlyList<ToolSet> broadResults,
        IReadOnlyList<ToolSet> explicitResults,
        InstallationDiscovery discovery,
        string subject)
    {
        ToolSet[] missing = explicitResults
            .Where(explicitResult => !broadResults.Any(
                broadResult => SameToolInstallation(broadResult, explicitResult)))
            .ToArray();
        if (missing.Length > 0)
        {
            discovery.Failures.Add(
                $"{subject} omitted explicit-root installations ({missing.Length}): "
                + string.Join("; ", missing.Select(Describe)));
            return;
        }

        ReportInvocationAliases(
            explicitResults.Select(explicitResult =>
            {
                ToolSet broadResult = broadResults.First(candidate =>
                    SameToolInstallation(candidate, explicitResult));
                return (explicitResult.CompilerPath, broadResult.CompilerPath);
            }),
            discovery,
            subject);
        int additionalCount = broadResults.Count(
            broadResult => !explicitResults.Any(
                explicitResult => SameToolInstallation(
                    broadResult,
                    explicitResult)));
        discovery.Decisions.Add(
            $"{subject} contains all {explicitResults.Count} explicit-root installations; "
            + $"additional installations: {additionalCount}.");
    }

    internal static void CompareSdks(
        IReadOnlyList<Sdk> broadResults,
        IReadOnlyList<Sdk> explicitResults,
        InstallationDiscovery discovery,
        string subject)
    {
        Sdk[] missing = explicitResults
            .Where(explicitResult => !broadResults.Any(
                broadResult => ContainsSdk(broadResult, explicitResult)))
            .ToArray();
        if (missing.Length > 0)
        {
            discovery.Failures.Add(
                $"{subject} did not contain explicit-root SDK installations "
                + $"with all layouts ({missing.Length} unmatched): "
                + string.Join("; ", missing.Select(Describe)));
            return;
        }

        ReportInvocationAliases(
            explicitResults.Select(explicitResult =>
            {
                Sdk broadResult = broadResults.First(candidate =>
                    ContainsSdk(candidate, explicitResult));
                return (explicitResult.CompilerPath, broadResult.CompilerPath);
            }),
            discovery,
            subject);
        int additionalInstallationCount = broadResults.Count(
            broadResult => !explicitResults.Any(
                explicitResult => SameSdkInstallation(
                    broadResult,
                    explicitResult)));
        int additionalLayoutCount = broadResults.Sum(
            broadResult => CountAdditionalLayouts(
                broadResult,
                explicitResults));
        discovery.Decisions.Add(
            $"{subject} contains all {explicitResults.Count} explicit-root installations; "
            + $"additional installations: {additionalInstallationCount}; "
            + $"additional layouts: {additionalLayoutCount}.");
    }

    internal static void ValidateExplicitToolSetInvocation(
        string explicitRoot,
        IReadOnlyList<ToolSet> toolSets,
        InstallationDiscovery discovery)
    {
        ValidateExplicitCompilerInvocation(
            explicitRoot,
            toolSets.Select(toolSet => toolSet.CompilerPath),
            discovery,
            "ToolSet");
    }

    internal static void ValidateExplicitSdkInvocation(
        string explicitRoot,
        IReadOnlyList<Sdk> sdks,
        InstallationDiscovery discovery,
        string subject)
    {
        ValidateExplicitCompilerInvocation(
            explicitRoot,
            sdks.Select(sdk => sdk.CompilerPath),
            discovery,
            subject);
    }

    private static void ReportInvocationAliases(
        IEnumerable<(string? ExplicitPath, string? BroadPath)> paths,
        InstallationDiscovery discovery,
        string subject)
    {
        var reported = new HashSet<string>(PathIdentity.Comparer);
        foreach ((string? explicitPath, string? broadPath) in paths)
        {
            if (explicitPath is null || broadPath is null)
            {
                continue;
            }

            string explicitInvocation = NormalizeInvocationPath(explicitPath);
            string broadInvocation = NormalizeInvocationPath(broadPath);
            if (PathIdentity.Comparer.Equals(
                explicitInvocation,
                broadInvocation)
                || !reported.Add(explicitInvocation + '\0' + broadInvocation))
            {
                continue;
            }

            discovery.Decisions.Add(
                $"{subject} matched explicit compiler invocation '{explicitInvocation}' "
                + $"through discovered alias '{broadInvocation}'.");
        }
    }

    private static void ValidateExplicitCompilerInvocation(
        string explicitRoot,
        IEnumerable<string?> compilerPaths,
        InstallationDiscovery discovery,
        string subject)
    {
        if (!File.Exists(explicitRoot))
        {
            return;
        }

        string expected = NormalizeInvocationPath(explicitRoot);
        if (compilerPaths.Any(path => path is null
            || !PathIdentity.Comparer.Equals(
                expected,
                NormalizeInvocationPath(path))))
        {
            discovery.Failures.Add(
                $"{subject} explicit-root discovery did not preserve compiler invocation "
                + $"'{expected}'.");
        }
    }

    private static bool SameToolInstallation(
        ToolSet left,
        ToolSet right) =>
        left.Kind == right.Kind
        && Equals(left.Version, right.Version)
        && Equals(left.ProductVersion, right.ProductVersion)
        && Equals(left.CompilerVersion, right.CompilerVersion)
        && SameTriple(left.DefaultTargetTriple, right.DefaultTargetTriple)
        && left.Channel == right.Channel
        && left.HostOS == right.HostOS
        && SameInstallationLocation(
            left.RootPath,
            left.EnvironmentPath,
            left.CompilerPath,
            right.RootPath,
            right.EnvironmentPath,
            right.CompilerPath);

    private static bool ContainsSdk(Sdk broad, Sdk expected) =>
        SameSdkInstallation(broad, expected)
        && expected.Layouts.All(expectedLayout =>
            broad.Layouts.Any(broadLayout =>
                SameLayout(broadLayout, expectedLayout)));

    private static bool SameSdkInstallation(Sdk left, Sdk right) =>
        left.Kind == right.Kind
        && Equals(left.Version, right.Version)
        && Equals(left.ProductVersion, right.ProductVersion)
        && left.Channel == right.Channel
        && SameInstallationLocation(
            left.RootPath,
            left.EnvironmentPath,
            left.CompilerPath,
            right.RootPath,
            right.EnvironmentPath,
            right.CompilerPath);

    private static bool SameInstallationLocation(
        string leftRoot,
        string leftEnvironment,
        string? leftCompiler,
        string rightRoot,
        string rightEnvironment,
        string? rightCompiler)
    {
        if (PathIdentity.AreEqual(leftRoot, rightRoot)
            && PathIdentity.AreEqual(leftEnvironment, rightEnvironment))
        {
            return true;
        }

        return leftCompiler is not null
            && rightCompiler is not null
            && PathIdentity.AreEqual(leftCompiler, rightCompiler);
    }

    private static int CountAdditionalLayouts(
        Sdk broad,
        IReadOnlyList<Sdk> explicitResults)
    {
        TargetLayout[] expectedLayouts = explicitResults
            .Where(explicitResult => SameSdkInstallation(
                broad,
                explicitResult))
            .SelectMany(explicitResult => explicitResult.Layouts)
            .ToArray();
        return expectedLayouts.Length == 0
            ? 0
            : broad.Layouts.Count(broadLayout =>
                !expectedLayouts.Any(expectedLayout =>
                    SameLayout(broadLayout, expectedLayout)));
    }

    private static bool SameLayout(
        TargetLayout left,
        TargetLayout right) =>
        left.Platform == right.Platform
        && left.Architecture == right.Architecture
        && SameTriple(left.TargetTriple, right.TargetTriple)
        && SameOptionalPath(left.SysrootPath, right.SysrootPath)
        && string.Equals(left.Multilib, right.Multilib, StringComparison.Ordinal)
        && Equals(
            left.MinimumDeploymentVersion,
            right.MinimumDeploymentVersion)
        && Equals(
            left.DefaultDeploymentVersion,
            right.DefaultDeploymentVersion)
        && left.ApiLevels.SequenceEqual(right.ApiLevels)
        && SameApiAliases(left.ApiAliases, right.ApiAliases)
        && SameResources(left.Resources, right.Resources);

    private static bool SameApiAliases(
        IReadOnlyDictionary<int, int> left,
        IReadOnlyDictionary<int, int> right) =>
        left.Count == right.Count
        && left.All(alias => right.TryGetValue(
            alias.Key,
            out int value)
            && value == alias.Value);

    private static bool SameResources(
        IReadOnlyList<Resource> left,
        IReadOnlyList<Resource> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!SameResource(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameResource(Resource left, Resource right) =>
        left.Purpose == right.Purpose
        && PathIdentity.AreEqual(left.Path, right.Path)
        && left.IsExternal == right.IsExternal
        && left.ApiLevel == right.ApiLevel
        && left.IsDirectory == right.IsDirectory;

    private static bool SameOptionalPath(string? left, string? right) =>
        left is null || right is null
            ? left is null && right is null
            : PathIdentity.AreEqual(left, right);

    private static bool SameTriple(string? left, string? right) =>
        string.Equals(
            CanonicalTriple(left),
            CanonicalTriple(right),
            StringComparison.Ordinal);

    private static string CanonicalTriple(string? triple) =>
        string.IsNullOrWhiteSpace(triple)
            ? string.Empty
            : TargetTripleIdentity.Canonicalize(triple);

    private static string NormalizeInvocationPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string Describe(ToolSet toolSet) =>
        $"{toolSet.Kind} {toolSet.Version?.ToString() ?? "unknown version"} "
        + $"{CanonicalTriple(toolSet.DefaultTargetTriple)} at '{toolSet.RootPath}' "
        + $"via '{toolSet.CompilerPath ?? "no compiler"}'";

    private static string Describe(Sdk sdk) =>
        $"{sdk.Kind} {sdk.Version?.ToString() ?? "unknown version"} "
        + $"at '{sdk.RootPath}' via '{sdk.CompilerPath ?? "no compiler"}' "
        + $"with [{string.Join(", ", sdk.Layouts.Select(Describe))}]";

    private static string Describe(TargetLayout layout) =>
        $"{layout.Platform}/{layout.Architecture}/"
        + $"{CanonicalTriple(layout.TargetTriple)}/"
        + $"{layout.Multilib ?? "no variant"}";
}
