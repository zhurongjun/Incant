namespace Incant.AutoTest.CppToolchain;

internal static class BuildStage
{
    internal static async Task<bool> ExecuteAsync(
        AutoTestContext context,
        CancellationToken cancellationToken)
    {
        var scheduler = new SerialBuildScheduler();
        foreach (ToolchainCandidate candidate in context.Candidates
            .Where(candidate => candidate.Status == CandidateStatus.Resolved))
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

            if (!context.ContinueAfter(candidate))
            {
                break;
            }
        }

        return context.RequiredCandidatesSatisfy(
            candidate => candidate.Status == CandidateStatus.Resolved);
    }
}
