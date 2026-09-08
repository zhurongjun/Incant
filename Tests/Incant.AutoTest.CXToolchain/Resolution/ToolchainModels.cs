using Incant.CX;
using Incant.CX.FindSdk;
using Incant.CX.FindTools;

namespace Incant.AutoTest.CXToolchain;

internal enum CandidateStatus
{
    Pending,
    Resolved,
    Invalid,
    BuildFailed,
    ExecutionFailed,
    Canceled,
    Passed,
    Skipped,
}

internal enum BuildAdapterKind
{
    Msvc,
    ClangCl,
    Gnu,
    Llvm,
    Apple,
    Android,
    Emscripten,
    Wasi,
}

internal enum LinkerFlavor
{
    Driver,
    Msvc,
    Lld,
}

internal enum ExecutionMode
{
    BuildOnly,
    Native,
    Node,
    Wasmtime,
}

internal sealed record ResolvedSdkComponent(
    string Role,
    Sdk Sdk,
    TargetLayout Layout);

internal sealed class ResolvedToolchain
{
    internal required string Id { get; init; }

    internal required IReadOnlyList<string> InstallationIds { get; init; }

    internal required BuildAdapterKind AdapterKind { get; init; }

    internal required LinkerFlavor LinkerFlavor { get; init; }

    internal required ToolSet ToolSet { get; init; }

    internal ToolSet? AuxiliaryToolSet { get; init; }

    internal required IReadOnlyList<ResolvedSdkComponent> Sdks { get; init; }

    internal required TargetPlatform TargetPlatform { get; init; }

    internal required TargetArchitecture TargetArchitecture { get; init; }

    internal required string TargetTriple { get; init; }

    internal required DriverConfiguration DriverConfiguration { get; init; }

    internal string? Multilib => DriverConfiguration.Multilib;

    internal int? AndroidApi { get; init; }

    internal required Tool CCompiler { get; init; }

    internal required Tool CppCompiler { get; init; }

    internal required Tool Archiver { get; init; }

    internal Tool? Ranlib { get; init; }

    internal Tool? Linker { get; init; }

    internal required IReadOnlyDictionary<string, string?> Environment { get; init; }

    internal required ExecutionMode ExecutionMode { get; init; }

    internal string? RuntimePath { get; init; }

    internal IEnumerable<Resource> Resources => Sdks.SelectMany(SelectResources);

    private IEnumerable<Resource> SelectResources(ResolvedSdkComponent component)
    {
        if (AndroidApi is not int requestedApi
            || component.Layout.Platform != TargetPlatform.Android)
        {
            return component.Layout.Resources;
        }

        int effectiveApi = component.Layout.ApiAliases.TryGetValue(
            requestedApi, out int aliasedApi)
            ? aliasedApi
            : requestedApi;
        return component.Layout.Resources.Where(resource =>
            resource.ApiLevel is null || resource.ApiLevel == effectiveApi);
    }
}

internal sealed class ToolchainCandidate
{
    internal ToolchainCandidate(string id, IEnumerable<string> installationIds, bool required = true)
    {
        Id = id;
        InstallationIds = installationIds.Distinct(StringComparer.Ordinal).ToArray();
        Required = required;
    }

    internal string Id { get; }

    internal IReadOnlyList<string> InstallationIds { get; }

    internal bool Required { get; }

    internal CandidateStatus Status { get; set; } = CandidateStatus.Pending;

    internal ResolvedToolchain? Toolchain { get; set; }

    internal BuildPlan? BuildPlan { get; set; }

    internal List<string> Decisions { get; } = [];

    internal List<string> Failures { get; } = [];

    internal List<BuildActionResult> Actions { get; } = [];

    internal bool Failed => Status is CandidateStatus.Invalid
        or CandidateStatus.BuildFailed
        or CandidateStatus.ExecutionFailed;

    internal void Invalidate(string reason)
    {
        Failures.Add(reason);
        Status = CandidateStatus.Invalid;
    }

    internal void Skip(string reason)
    {
        Decisions.Add(reason);
        Status = CandidateStatus.Skipped;
    }
}
