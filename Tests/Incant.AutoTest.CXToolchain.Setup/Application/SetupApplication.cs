namespace Incant.AutoTest.CXToolchain.Setup;

internal static class SetupApplication
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        SetupParseResult parsed = SetupCommandLine.Parse(arguments);
        if (parsed.Options is not SetupOptions options)
        {
            return parsed.ExitCode;
        }

        SetupContext context;
        try
        {
            context = new SetupContext(options);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            Console.Error.WriteLine($"[setup:configuration] {exception.Message}");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            await new SerialSetupPipeline().RunAsync(
                context, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            context.RecordCancellation();
        }
        catch (SetupConfigurationException exception)
        {
            context.RecordFailure(exception.Message, exitCode: 2);
            Console.Error.WriteLine($"[setup:configuration] {exception.Message}");
        }
        catch (Exception exception)
        {
            context.RecordFailure(
                $"The Setup pipeline could not continue: {exception.Message}");
            Console.Error.WriteLine(exception);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            context.CompletedAt = DateTimeOffset.UtcNow;
            try
            {
                await SetupReportWriter.WriteAsync(
                    context, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                context.RecordFailure(
                    $"The Setup report could not be written: {exception.Message}");
                Console.Error.WriteLine(
                    $"[setup:report-failure] {exception}");
            }
        }

        PrintSummary(context);
        return context.ExitCode;
    }

    private static void PrintSummary(SetupContext context)
    {
        Console.WriteLine($"Profile: {context.Profile.Name}");
        foreach (SetupStageRecord stage in context.Stages)
        {
            Console.WriteLine(
                $"  {stage.Name,-20} {stage.Status,-10} {stage.Elapsed.TotalSeconds:F2}s");
        }

        foreach (SetupComponentRecord component in context.Components)
        {
            Console.WriteLine($"  {component.Status,-10} {component.Id}");
            if (component.Error is not null)
            {
                Console.Error.WriteLine($"    {component.Error.Message}");
            }

            if (component.SkipReason is not null)
            {
                Console.Error.WriteLine($"    {component.SkipReason}");
            }
        }

        Console.WriteLine($"Report: {context.Options.ReportPath}");
        if (context.EnvironmentExported)
        {
            Console.WriteLine($"Environment: {context.Options.EnvironmentPath}");
        }

        if (context.FatalError is not null)
        {
            Console.Error.WriteLine(context.FatalError);
        }
    }
}
