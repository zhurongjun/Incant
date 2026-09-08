using System.Diagnostics;

namespace Incant.AutoTest.CXToolchain;

internal sealed class SerialTestPipeline(IReadOnlyList<PipelineStage> stages)
{
    internal static SerialTestPipeline Create()
    {
        return new SerialTestPipeline(Enum.GetValues<PipelineStageKind>().Select(CreateStage).ToArray());
    }

    internal async Task RunAsync(AutoTestContext context, CancellationToken cancellationToken)
    {
        bool canceled = false;
        foreach (PipelineStage stage in stages)
        {
            var result = new PipelineStageResult { Name = stage.Name };
            context.Stages.Add(result);
            if (context.IsFatal || canceled)
            {
                result.Status = PipelineStageStatus.Skipped;
                result.Messages.Add(canceled
                    ? "The pipeline was canceled."
                    : "A previous stage reported a fatal failure.");
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

        if (!context.IsFatal && !canceled && !ToolchainCoverage.Evaluate(context))
        {
            context.RecordTestFailure();
        }
    }

    private static PipelineStage CreateStage(PipelineStageKind kind) => kind switch
    {
        PipelineStageKind.Preflight => new PipelineStage(kind, PreflightStage.ExecuteAsync),
        PipelineStageKind.Discover => new PipelineStage(kind, DiscoveryStage.ExecuteAsync),
        PipelineStageKind.Resolve => new PipelineStage(kind, ResolveStage.ExecuteAsync),
        PipelineStageKind.Build => new PipelineStage(kind, BuildStage.ExecuteAsync),
        PipelineStageKind.Execute => new PipelineStage(kind, ExecuteStage.ExecuteAsync),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
