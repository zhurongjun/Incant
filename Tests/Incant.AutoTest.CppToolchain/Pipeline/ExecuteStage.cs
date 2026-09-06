namespace Incant.AutoTest.CppToolchain;

internal static class ExecuteStage
{
    internal static async Task<bool> ExecuteAsync(
        AutoTestContext context,
        CancellationToken cancellationToken)
    {
        var scheduler = new SerialBuildScheduler();
        foreach (ToolchainCandidate candidate in context.Candidates
            .Where(candidate => candidate.Status == CandidateStatus.Resolved))
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
            if (!context.ContinueAfter(candidate))
            {
                break;
            }
        }

        return context.RequiredCandidatesSatisfy(
            candidate => candidate.Status == CandidateStatus.Passed);
    }
}
