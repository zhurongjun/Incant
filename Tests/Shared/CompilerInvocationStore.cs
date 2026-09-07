using System.Diagnostics;
using System.Text.Json;

namespace Incant.TestSupport;

/// <summary>Publishes immutable invocation events; concurrent processes never share a writable log file.</summary>
internal static class CompilerInvocationStore
{
    internal static async Task<CompilerInvocation> StartAsync(string directory, IReadOnlyList<string> arguments)
    {
        var invocation = new CompilerInvocation(Guid.NewGuid(), Environment.ProcessId,
            arguments.ToArray(), Stopwatch.GetTimestamp());
        await PublishAsync(directory, invocation, "started").ConfigureAwait(false);
        return invocation;
    }

    internal static Task CompleteAsync(string directory, CompilerInvocation invocation, int exitCode) =>
        PublishAsync(directory, invocation with
        {
            CompletedTimestamp = Stopwatch.GetTimestamp(),
            ExitCode = exitCode,
        }, "completed");

    internal static IReadOnlyList<CompilerInvocation> Read(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var invocations = new List<CompilerInvocation>();
        foreach (string started in Directory.EnumerateFiles(directory, "*.started.json").Order(StringComparer.Ordinal))
        {
            CompilerInvocation invocation = ReadFile(started);
            string completed = Path.Combine(directory, $"{invocation.Id:N}.completed.json");
            if (File.Exists(completed))
            {
                CompilerInvocation final = ReadFile(completed);
                if (final.Id != invocation.Id || final.ProcessId != invocation.ProcessId
                    || final.StartedTimestamp != invocation.StartedTimestamp
                    || !final.Arguments.SequenceEqual(invocation.Arguments)
                    || final.CompletedTimestamp is not long timestamp || timestamp < final.StartedTimestamp
                    || final.ExitCode is null)
                {
                    throw new InvalidDataException($"Inconsistent compiler invocation event: '{completed}'.");
                }

                invocation = final;
            }

            invocations.Add(invocation);
        }

        return invocations.OrderBy(invocation => invocation.StartedTimestamp).ThenBy(invocation => invocation.Id).ToArray();
    }

    private static CompilerInvocation ReadFile(string path)
    {
        try
        {
            CompilerInvocation? invocation = JsonSerializer.Deserialize<CompilerInvocation>(File.ReadAllText(path));
            if (invocation is null || invocation.Id == Guid.Empty || invocation.ProcessId <= 0
                || invocation.Arguments is null || invocation.Arguments.Any(argument => argument is null))
            {
                throw new InvalidDataException($"Invalid compiler invocation record: '{path}'.");
            }

            return invocation;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Malformed compiler invocation record: '{path}'.", exception);
        }
    }

    private static async Task PublishAsync(string directory, CompilerInvocation invocation, string phase)
    {
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, $"{invocation.Id:N}.{phase}.json");
        string temporary = destination + ".tmp";
        try
        {
            await using (FileStream stream = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, invocation).ConfigureAwait(false);
            }

            File.Move(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
