using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Incant.Base;

namespace Incant.Core.Cpp;

/// <summary>Supplies one immutable environment snapshot and bounded, read-only process probes.</summary>
public sealed class DiscoveryContext
{
    /// <summary>Captures the supplied replacement environment, or the current process environment.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive and finite.</exception>
    public DiscoveryContext(
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? probeTimeout = null)
    {
        ProbeTimeout = probeTimeout ?? TimeSpan.FromSeconds(10);
        if (ProbeTimeout <= TimeSpan.Zero || ProbeTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(probeTimeout));
        }

        var values = new Dictionary<string, string?>(SearchPaths.Comparer);
        if (environment is null)
        {
            foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            {
                values[(string)entry.Key] = entry.Value?.ToString();
            }
        }
        else
        {
            foreach ((string key, string? value) in environment)
            {
                values.Add(key, value);
            }
        }

        Environment = new ReadOnlyDictionary<string, string?>(values);
    }

    /// <summary>Gets the frozen replacement environment used by every child process.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; }

    /// <summary>Gets the maximum duration of an individual probe.</summary>
    public TimeSpan ProbeTimeout { get; }

    /// <summary>Gets the host operating system.</summary>
    public PlatformOS HostOS => Platform.OS;

    /// <summary>Gets the current process architecture.</summary>
    public TargetArchitecture HostArchitecture => Platform.Arch switch
    {
        PlatformArch.X86 => TargetArchitecture.X86,
        PlatformArch.X64 => TargetArchitecture.X64,
        PlatformArch.ARM64 => TargetArchitecture.ARM64,
        _ => TargetArchitecture.Unknown,
    };

    /// <summary>Returns an environment value, or null when it is absent.</summary>
    public string? GetEnvironmentVariable(string name) => Environment.GetValueOrDefault(name);

    /// <summary>
    /// Runs a read-only executable entry directly in the captured environment with a fixed English locale.
    /// A nonzero exit, timeout or process start failure returns null. Cancellation propagates. The operating system may honor an entry's launcher format; no implicit command shell is inserted.
    /// </summary>
    public async Task<ProcessResult?> ProbeAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        ProbeOutcome outcome = await ProbeDetailedAsync(
            executablePath, arguments, cancellationToken, overrides).ConfigureAwait(false);
        return outcome.Status == ProbeStatus.Success ? outcome.Result : null;
    }

    internal async Task<ProbeOutcome> ProbeDetailedAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] argumentSnapshot = arguments.ToArray();
        var environment = new Dictionary<string, string?>(SearchPaths.Comparer);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = null;
        }

        foreach ((string key, string? value) in Environment)
        {
            environment[key] = value;
        }

        if (overrides is not null)
        {
            foreach ((string key, string? value) in overrides)
            {
                environment[key] = value;
            }
        }

        environment["LC_ALL"] = "C";
        environment["LANG"] = "C";

        long started = Stopwatch.GetTimestamp();
        try
        {
            ProcessResult result = await Misc.RunProcessAsync(executablePath, argumentSnapshot, new ProcessOptions
            {
                Environment = environment,
                Timeout = ProbeTimeout,
                EnsureUnixExecutablePermission = false,
            }, cancellationToken).ConfigureAwait(false);
            return new ProbeOutcome(executablePath, argumentSnapshot, result, result.Elapsed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ProbeOutcome(executablePath, argumentSnapshot, null,
                Stopwatch.GetElapsedTime(started), exception.Message);
        }
    }
}
