using Incant.Core.Cpp;
using SdkKind = Incant.Core.Cpp.FindSdk.Kind;
using ToolKind = Incant.Core.Cpp.FindTools.Kind;

namespace Incant.AutoTest.CppToolchain;

internal sealed record CoverageResult(string Requirement, bool Passed, IReadOnlyList<string> ScenarioIds, string? Reason);

internal static class ToolchainCoverage
{
    internal static bool Evaluate(AutoTestContext context, bool completed)
    {
        context.Coverage.Clear();
        bool Satisfies(ToolchainCandidate candidate) => completed
            ? candidate.Status == CandidateStatus.Passed
            : candidate.Status == CandidateStatus.Resolved;

        foreach (InstallationRequirement requirement in context.Profile.Installations.Where(item => item.Required))
        {
            ToolchainCandidate[] candidates = context.Candidates.Where(candidate =>
                candidate.Required && candidate.InstallationIds.Contains(requirement.Id)).ToArray();
            bool passed = candidates.Length > 0 && candidates.All(Satisfies);
            context.Coverage.Add(new CoverageResult(requirement.Id, passed,
                candidates.Select(candidate => candidate.Id).ToArray(),
                passed ? null : "A declared installation or required scenario has no successful result."));
        }

        foreach (InstallationKind family in context.Profile.Definition.RequiredHostFamilies)
        {
            ToolchainCandidate[] candidates = context.Candidates.Where(candidate =>
                candidate.Toolchain is ResolvedToolchain toolchain
                && toolchain.TargetArchitecture == context.Profile.HostArchitecture
                && toolchain.ExecutionMode == ExecutionMode.Native && Covers(toolchain, family)).ToArray();
            bool passed = candidates.Any(Satisfies);
            context.Coverage.Add(new CoverageResult("host/" + family, passed,
                candidates.Select(candidate => candidate.Id).ToArray(),
                passed ? null : "No usable host scenario covers this required family."));
        }

        return context.Coverage.All(result => result.Passed);
    }

    private static bool Covers(ResolvedToolchain toolchain, InstallationKind family) => family switch
    {
        InstallationKind.VisualStudio => toolchain.ToolSet.Kind == ToolKind.VisualStudio,
        InstallationKind.WindowsSdk => toolchain.Sdks.Any(component => component.Sdk.Kind == SdkKind.Windows),
        InstallationKind.Gnu => toolchain.ToolSet.Kind == ToolKind.Gnu,
        InstallationKind.Llvm => toolchain.ToolSet.Kind == ToolKind.Llvm,
        InstallationKind.Xcode => toolchain.ToolSet.Kind == ToolKind.Xcode,
        _ => false,
    };
}
