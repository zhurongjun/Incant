namespace Incant.AutoTest.CppToolchain;

internal static class BuildStage
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
            try
            {
                candidate.BuildPlan = BuildAdapterFactory.Create(
                    candidate.Toolchain!).CreatePlan(context, candidate.Toolchain!);
                await scheduler.RunAsync(
                    context,
                    candidate,
                    BuildActionPhase.Build,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                candidate.Status = CandidateStatus.BuildFailed;
                candidate.Failures.Add(
                    $"Build plan creation failed: {exception.Message}");
            }
        }

        return context.CandidatesSatisfy(
            candidates,
            candidate => candidate.Status == CandidateStatus.Resolved);
    }
}
