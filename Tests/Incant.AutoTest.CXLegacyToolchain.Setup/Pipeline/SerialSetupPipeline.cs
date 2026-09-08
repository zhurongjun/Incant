using System.Diagnostics;

namespace Incant.AutoTest.CXLegacyToolchain.Setup;

internal sealed class SerialSetupPipeline
{
    internal async Task RunAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunRequiredStageAsync(
                context,
                "Preflight",
                token => SetupStageOperations.PreflightAsync(context, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            SkipStage(context, "Host toolchains", "Preflight did not succeed.");
            SkipStage(context, "Bundle toolchains", "Preflight did not succeed.");
            SkipStage(context, "Write manifest", "Preflight did not succeed.");
            SkipStage(context, "Build AutoTest", "Preflight did not succeed.");
            SkipStage(context, "Export environment", "Preflight did not succeed.");
            throw;
        }

        await RunComponentStageAsync(
            context,
            "Host toolchains",
            HostToolchainComponents.Create(context.Profile),
            cancellationToken).ConfigureAwait(false);
        await RunComponentStageAsync(
            context,
            "Bundle toolchains",
            BundleToolchainComponents.Create(context.Profile),
            cancellationToken).ConfigureAwait(false);

        if (context.HasComponentFailures)
        {
            SkipStage(
                context,
                "Write manifest",
                "One or more required provisioning components failed.");
        }
        else
        {
            await RunStageAsync(
                context,
                "Write manifest",
                token => SetupStageOperations.PrepareManifestAsync(context, token),
                cancellationToken).ConfigureAwait(false);
        }

        if (!context.ManifestPrepared)
        {
            SkipStage(context, "Build AutoTest", "The environment manifest was not prepared.");
        }
        else
        {
            await RunStageAsync(
                context,
                "Build AutoTest",
                token => SetupStageOperations.BuildAutoTestAsync(context, token),
                cancellationToken).ConfigureAwait(false);
        }

        if (!context.BuildSucceeded)
        {
            SkipStage(context, "Export environment", "The AutoTest build did not succeed.");
        }
        else
        {
            await RunStageAsync(
                context,
                "Export environment",
                token => SetupStageOperations.ExportEnvironmentAsync(context, token),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RunRequiredStageAsync(
        SetupContext context,
        string name,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        SetupStageRecord stage = StartStage(context, name);
        long started = Stopwatch.GetTimestamp();
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
            stage.Status = SetupResultStatus.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stage.Status = SetupResultStatus.Cancelled;
            throw;
        }
        catch (SetupConfigurationException exception)
        {
            stage.Status = SetupResultStatus.Failed;
            stage.Error = SetupErrorRecord.FromException(exception);
            throw;
        }
        catch (Exception exception)
        {
            var configuration = new SetupConfigurationException(
                $"Setup preflight failed: {exception.Message}",
                exception);
            stage.Status = SetupResultStatus.Failed;
            stage.Error = SetupErrorRecord.FromException(configuration);
            throw configuration;
        }
        finally
        {
            CompleteStage(context, stage, started);
        }
    }

    private static async Task RunComponentStageAsync(
        SetupContext context,
        string name,
        IReadOnlyList<ISetupComponent> components,
        CancellationToken cancellationToken)
    {
        SetupStageRecord stage = StartStage(context, name);
        long started = Stopwatch.GetTimestamp();
        try
        {
            await SetupComponentExecutor.RunAsync(
                context, name, components, cancellationToken).ConfigureAwait(false);
            stage.Status = context.Components.Any(component =>
                component.Stage == name && component.Status is
                    SetupResultStatus.Failed or
                    SetupResultStatus.Skipped)
                ? SetupResultStatus.Failed
                : SetupResultStatus.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stage.Status = SetupResultStatus.Cancelled;
            throw;
        }
        finally
        {
            CompleteStage(context, stage, started);
        }
    }

    private static async Task RunStageAsync(
        SetupContext context,
        string name,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        SetupStageRecord stage = StartStage(context, name);
        long started = Stopwatch.GetTimestamp();
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
            stage.Status = SetupResultStatus.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stage.Status = SetupResultStatus.Cancelled;
            throw;
        }
        catch (Exception exception)
        {
            stage.Status = SetupResultStatus.Failed;
            stage.Error = SetupErrorRecord.FromException(exception);
            context.RecordFailure($"Stage '{name}' failed: {exception.Message}");
            Console.Error.WriteLine($"[stage:failure] name={name} error={exception.Message}");
        }
        finally
        {
            CompleteStage(context, stage, started);
        }
    }

    private static SetupStageRecord StartStage(SetupContext context, string name)
    {
        SetupStageRecord stage = context.AddStage(name);
        stage.Status = SetupResultStatus.Running;
        stage.StartedAt = DateTimeOffset.UtcNow;
        context.ActiveStage = name;
        Console.WriteLine($"::group::Setup: {name}");
        return stage;
    }

    private static void CompleteStage(
        SetupContext context,
        SetupStageRecord stage,
        long started)
    {
        stage.Elapsed = Stopwatch.GetElapsedTime(started);
        stage.CompletedAt = DateTimeOffset.UtcNow;
        context.ActiveStage = null;
        Console.WriteLine(
            $"[stage:end] name={stage.Name} status={stage.Status} "
            + $"durationMs={stage.Elapsed.TotalMilliseconds:F0}");
        Console.WriteLine("::endgroup::");
    }

    private static void SkipStage(SetupContext context, string name, string reason)
    {
        SetupStageRecord stage = context.AddStage(name);
        stage.Status = SetupResultStatus.Skipped;
        stage.SkipReason = reason;
        Console.Error.WriteLine($"[stage:skip] name={name} reason={reason}");
    }
}
