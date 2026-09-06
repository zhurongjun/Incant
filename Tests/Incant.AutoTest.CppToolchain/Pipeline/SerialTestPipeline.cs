using System.Diagnostics;

namespace Incant.AutoTest.CppToolchain;

internal sealed class SerialTestPipeline(IReadOnlyList<PipelineStage> stages)
{
    internal static SerialTestPipeline Create(EnvironmentProfile profile)
    {
        PipelineStageKind[] requiredOrder = Enum.GetValues<PipelineStageKind>();
        if (!profile.PipelineStages.SequenceEqual(requiredOrder))
        {
            throw new AutoTestConfigurationException(
                $"Profile '{profile.Name}' must declare every pipeline stage in the required order: "
                + string.Join(", ", requiredOrder) + ".");
        }

        return new SerialTestPipeline(profile.PipelineStages.Select(CreateStage).ToArray());
    }

    internal async Task RunAsync(AutoTestContext context, CancellationToken cancellationToken)
    {
        bool stopAfterStageFailure = false;
        bool canceled = false;
        foreach (PipelineStage stage in stages)
        {
            var result = new PipelineStageResult { Name = stage.Name };
            context.Stages.Add(result);
            if (context.IsFatal || stopAfterStageFailure || canceled)
            {
                result.Status = PipelineStageStatus.Skipped;
                result.Messages.Add(canceled
                    ? "The pipeline was canceled."
                    : context.IsFatal
                        ? "A previous stage reported a fatal failure."
                        : "The profile stops after a failed stage.");
                continue;
            }

            long started = Stopwatch.GetTimestamp();
            try
            {
                bool succeeded = await stage.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
                result.Status = succeeded ? PipelineStageStatus.Passed : PipelineStageStatus.Failed;
                if (!succeeded)
                {
                    context.RecordTestFailure();
                    stopAfterStageFailure = !context.Profile.FailurePolicy.ContinueAfterStageFailure;
                }
            }
            catch (AutoTestConfigurationException exception)
            {
                result.Status = PipelineStageStatus.Failed;
                result.Messages.Add(exception.Message);
                context.SetFatal(exception.Message, 2);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result.Status = PipelineStageStatus.Canceled;
                result.Messages.Add("The pipeline was canceled.");
                context.RecordCancellation();
                canceled = true;
            }
            catch (Exception exception)
            {
                result.Status = PipelineStageStatus.Failed;
                result.Messages.Add(exception.ToString());
                context.SetFatal(exception.Message, 1);
            }
            finally
            {
                result.Elapsed = Stopwatch.GetElapsedTime(started);
            }
        }
    }

    private static PipelineStage CreateStage(PipelineStageKind kind) => kind switch
    {
        PipelineStageKind.Preflight => new PipelineStage(kind, PreflightStage.ExecuteAsync),
        PipelineStageKind.Discover => new PipelineStage(kind, DiscoveryStage.ExecuteAsync),
        PipelineStageKind.Resolve => new PipelineStage(kind, ResolveStage.ExecuteAsync),
        PipelineStageKind.Validate => new PipelineStage(kind, ValidationStage.ExecuteAsync),
        PipelineStageKind.Build => new PipelineStage(kind, BuildStage.ExecuteAsync),
        PipelineStageKind.Execute => new PipelineStage(kind, ExecuteStage.ExecuteAsync),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
