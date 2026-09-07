namespace Incant.AutoTest.CppToolchain;

internal static class ExecuteStage
{
    internal static async Task<bool> ExecuteAsync(
        AutoTestContext context,
        CancellationToken cancellationToken)
    {
        var scheduler = new SerialBuildScheduler();
        ToolchainCandidate[] candidates = context.Candidates
            .Where(candidate => candidate.Status == CandidateStatus.Resolved)
            .ToArray();
        foreach (ToolchainCandidate candidate in candidates)
        {
            BuildAction[] actions = candidate.BuildPlan!.Actions
                .Where(action => action.Phase == BuildActionPhase.Execute)
                .ToArray();
            if (actions.Length == 0)
            {
                candidate.Status = CandidateStatus.Passed;
                candidate.Decisions.Add(
                    "Execution was skipped because this target is build-only on the current host.");
            }
            else if (await scheduler.RunAsync(
                context,
                candidate,
                BuildActionPhase.Execute,
                cancellationToken).ConfigureAwait(false))
            {
                candidate.Status = CandidateStatus.Passed;
            }

            SerialBuildScheduler.DeleteSuccessfulWork(context, candidate);
        }

        return context.CandidatesSatisfy(
            candidates,
            candidate => candidate.Status == CandidateStatus.Passed);
    }
}
