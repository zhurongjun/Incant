using System.Diagnostics;

namespace Incant.AutoTest.CXToolchain.Setup;

internal static class SetupComponentExecutor
{
    internal static async Task RunAsync(
        SetupContext context,
        string stage,
        IReadOnlyList<ISetupComponent> components,
        CancellationToken cancellationToken)
    {
        foreach (ISetupComponent component in components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetupComponentRecord record = context.AddComponent(stage, component);
            string[] unavailableDependencies = component.Dependencies
                .Where(dependency =>
                    context.GetComponentStatus(dependency) != SetupResultStatus.Succeeded)
                .ToArray();
            if (unavailableDependencies.Length > 0)
            {
                record.Status = SetupResultStatus.Skipped;
                record.SkipReason =
                    $"Dependencies did not succeed: {string.Join(", ", unavailableDependencies)}.";
                context.SetComponentStatus(component.Id, record.Status);
                context.RecordFailure(
                    $"Component '{component.Name}' was skipped: {record.SkipReason}");
                Console.Error.WriteLine(
                    $"[component:skip] id={component.Id} reason={record.SkipReason}");
                continue;
            }

            record.Status = SetupResultStatus.Running;
            record.StartedAt = DateTimeOffset.UtcNow;
            context.ActiveComponentId = component.Id;
            long started = Stopwatch.GetTimestamp();
            Console.WriteLine($"::group::Setup component: {component.Name}");
            Console.WriteLine($"[component:start] id={component.Id}");
            try
            {
                ProvisioningResult result = await component.ProvisionAsync(
                    context, cancellationToken).ConfigureAwait(false);
                context.Merge(result);
                record.Status = SetupResultStatus.Succeeded;
                Console.WriteLine($"[component:success] id={component.Id}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                record.Status = SetupResultStatus.Cancelled;
                throw;
            }
            catch (Exception exception)
            {
                record.Status = SetupResultStatus.Failed;
                record.Error = SetupErrorRecord.FromException(exception);
                context.RecordFailure(
                    $"Component '{component.Name}' failed: {exception.Message}");
                Console.Error.WriteLine(
                    $"[component:failure] id={component.Id} error={exception.Message}");
                PrintCommandFailure(exception);
            }
            finally
            {
                record.Elapsed = Stopwatch.GetElapsedTime(started);
                record.CompletedAt = DateTimeOffset.UtcNow;
                context.ActiveComponentId = null;
                context.SetComponentStatus(component.Id, record.Status);
                Console.WriteLine(
                    $"[component:end] id={component.Id} status={record.Status} "
                    + $"durationMs={record.Elapsed.TotalMilliseconds:F0}");
                Console.WriteLine("::endgroup::");
            }
        }
    }

    private static void PrintCommandFailure(Exception exception)
    {
        SetupCommandException? commandException = FindCommandException(exception);
        if (commandException is null)
        {
            return;
        }

        SetupCommandRecord command = commandException.Command;
        Console.Error.WriteLine(
            $"[component:command] file={command.File} exit={command.ExitCode?.ToString() ?? "none"} "
            + $"stdoutLog={command.StandardOutputLog} stderrLog={command.StandardErrorLog}");
        if (!string.IsNullOrWhiteSpace(command.StandardOutputTail))
        {
            Console.Error.WriteLine("[component:stdout-tail]");
            Console.Error.WriteLine(command.StandardOutputTail.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(command.StandardErrorTail))
        {
            Console.Error.WriteLine("[component:stderr-tail]");
            Console.Error.WriteLine(command.StandardErrorTail.TrimEnd());
        }
    }

    private static SetupCommandException? FindCommandException(Exception exception)
    {
        Exception? current = exception;
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        while (current is not null && visited.Add(current))
        {
            if (current is SetupCommandException commandException)
            {
                return commandException;
            }

            current = current.InnerException;
        }

        return null;
    }
}
