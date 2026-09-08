using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Incant.AutoTest.CXToolchain.Setup;

internal static class SetupReportWriter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static async Task WriteAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        object report = new
        {
            SchemaVersion = 1,
            Profile = context.Profile.Name,
            Status = Status(context),
            ExitCode = context.ExitCode,
            StartedAt = context.StartedAt,
            CompletedAt = context.CompletedAt,
            Host = new
            {
                OS = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                Framework = RuntimeInformation.FrameworkDescription,
                ImageOS = Environment.GetEnvironmentVariable("ImageOS") ?? string.Empty,
                ImageVersion = Environment.GetEnvironmentVariable("ImageVersion") ?? string.Empty,
                RunnerOS = Environment.GetEnvironmentVariable("RUNNER_OS") ?? string.Empty,
                RunnerArchitecture = Environment.GetEnvironmentVariable("RUNNER_ARCH") ?? string.Empty,
            },
            Paths = new
            {
                context.Options.Workspace,
                context.Options.ToolchainRoot,
                WorkRoot = context.Options.WorkRoot,
                Manifest = context.Options.EnvironmentPath,
                Report = context.Options.ReportPath,
            },
            Stages = context.Stages.Select(stage => new
            {
                stage.Name,
                stage.Status,
                stage.StartedAt,
                stage.CompletedAt,
                DurationMilliseconds = stage.Elapsed.TotalMilliseconds,
                stage.SkipReason,
                Error = ErrorSnapshot(stage.Error),
            }),
            Components = context.Components.Select(component => new
            {
                component.Id,
                component.Name,
                component.Stage,
                component.Dependencies,
                component.Status,
                component.StartedAt,
                component.CompletedAt,
                DurationMilliseconds = component.Elapsed.TotalMilliseconds,
                component.SkipReason,
                Error = ErrorSnapshot(component.Error),
            }),
            Commands = context.CommandRecords.Select(command => new
            {
                command.Sequence,
                command.File,
                command.Arguments,
                command.WorkingDirectory,
                command.Stage,
                command.ComponentId,
                command.Environment,
                command.Status,
                command.ExitCode,
                command.TimedOut,
                command.StartedAt,
                command.CompletedAt,
                DurationMilliseconds = command.Elapsed.TotalMilliseconds,
                command.StandardOutputLog,
                command.StandardErrorLog,
                command.StandardOutputTail,
                command.StandardErrorTail,
                Error = ErrorSnapshot(command.Error),
            }),
            Failure = context.FatalError,
            ManifestPrepared = context.ManifestPrepared,
            AutoTestBuilt = context.BuildSucceeded,
            EnvironmentExported = context.EnvironmentExported,
            context.Installations,
            context.Runtimes,
        };

        string path = Path.GetFullPath(context.Options.ReportPath);
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The setup report path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream, report, s_jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }

        Console.WriteLine($"[setup:report] {path}");
    }

    private static string Status(SetupContext context)
    {
        if (context.ExitCode == 130)
        {
            return "cancelled";
        }

        return context.ExitCode == 0 && context.EnvironmentExported
            ? "succeeded"
            : "failed";
    }

    private static object? ErrorSnapshot(SetupErrorRecord? error) => error is null
        ? null
        : new
        {
            error.Type,
            error.Message,
            error.StackTrace,
            InnerError = ErrorSnapshot(error.InnerError),
        };
}
