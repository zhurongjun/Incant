using System.Text;
using System.Text.Json;
using Incant.Base;

namespace Incant.CX;

internal enum ProbeStatus
{
    Success,
    NonzeroExit,
    TimedOut,
    StartFailed,
}

internal sealed record ProbeOutcome(
    string Path,
    IReadOnlyList<string> Arguments,
    ProcessResult? Result,
    TimeSpan Elapsed,
    string? Error = null)
{
    internal ProbeStatus Status => Result is null ? ProbeStatus.StartFailed
        : Result.TimedOut ? ProbeStatus.TimedOut
        : Result.IsSuccess ? ProbeStatus.Success : ProbeStatus.NonzeroExit;

    internal string Describe(string? reason = null) =>
        $"{reason ?? Status.ToString()}: {Path} {JsonSerializer.Serialize(Arguments)}; "
        + $"exit={Result?.ExitCode?.ToString() ?? "none"}; elapsed={Elapsed.TotalMilliseconds:F0}ms"
        + (Error is null ? "" : $"; error={Error}")
        + (Result is null ? "" : $"\nstdout: {Tail(Result.StandardOutput)}\nstderr: {Tail(Result.StandardError)}");

    private static string Tail(string value)
    {
        const int MaximumBytes = 4096;
        int start = Math.Max(0, value.Length - MaximumBytes);
        while (Encoding.UTF8.GetByteCount(value.AsSpan(start)) > MaximumBytes)
        {
            start++;
        }

        if (start < value.Length && char.IsLowSurrogate(value[start]))
        {
            start++;
        }

        return value[start..];
    }
}
