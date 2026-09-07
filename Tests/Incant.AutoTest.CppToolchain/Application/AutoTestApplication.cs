namespace Incant.AutoTest.CppToolchain;

internal static class AutoTestApplication
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        AutoTestParseResult parsed = AutoTestCommandLine.Parse(arguments);
        if (parsed.Options is not AutoTestOptions options)
        {
            return parsed.ExitCode;
        }

        var context = new AutoTestContext(options);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            await SerialTestPipeline.Create().RunAsync(
                context, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            context.RecordCancellation();
        }
        catch (AutoTestConfigurationException exception)
        {
            context.SetFatal(exception.Message, 2);
        }
        catch (Exception exception)
        {
            context.SetFatal($"The AutoTest pipeline could not start: {exception.Message}", 1);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            context.CompletedAt = DateTimeOffset.UtcNow;
            try
            {
                await AutoTestReportWriter.WriteAsync(
                    context, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                context.SetFatal(
                    $"The AutoTest report could not be written: {exception.Message}",
                    1);
            }
        }

        PrintSummary(context);
        return context.ExitCode;
    }

    private static void PrintSummary(AutoTestContext context)
    {
        Console.WriteLine($"Profile: {context.Profile.Name}");
        foreach (PipelineStageResult stage in context.Stages)
        {
            Console.WriteLine(
                $"  {stage.Name,-10} {stage.Status,-8} {stage.Elapsed.TotalSeconds:F2}s");
        }

        foreach (ToolchainCandidate candidate in context.Candidates)
        {
            Console.WriteLine($"  {candidate.Status,-15} {candidate.Id}");
            foreach (string failure in candidate.Failures)
            {
                Console.Error.WriteLine($"    {failure}");
            }
        }

        foreach (CoverageResult coverage in context.Coverage.Where(result => !result.Passed))
        {
            Console.Error.WriteLine($"  Coverage failed: {coverage.Requirement}: {coverage.Reason}");
        }

        Console.WriteLine($"Report: {context.Options.ReportPath}");
        if (context.FatalError is not null)
        {
            Console.Error.WriteLine(context.FatalError);
        }
    }
}
