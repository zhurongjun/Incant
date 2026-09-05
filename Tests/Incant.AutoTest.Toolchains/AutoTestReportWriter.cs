using System.Text.Json;
using System.Text.Json.Serialization;

internal static class AutoTestReportWriter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static void Write(string path, AutoTestCommand command, IReadOnlyCollection<AutoTestRun> runs, bool success, string? error)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var report = new
        {
            Success = success,
            Error = error,
            Command = command.Operation.ToString(),
            command.Kind,
            command.Target,
            command.Architecture,
            command.ClangClLinker,
            command.RequiredComponents,
            Runs = runs.Select(run => new
            {
                run.Name,
                run.DiscoveredInstallationCount,
                run.ToolSets,
                run.Sdks,
                run.Diagnostics,
                Configuration = run.Configuration is not SmokeConfiguration configuration ? null : new
                {
                    ToolSet = configuration.ToolSet.RootPath,
                    Sdk = configuration.Sdk?.RootPath,
                    CompilerSdk = configuration.CompilerSdk?.RootPath,
                    Msvc = configuration.MsvcToolSet?.RootPath,
                    configuration.TargetPlatform,
                    configuration.TargetArchitecture,
                    configuration.TargetTriple,
                    configuration.Layout.Multilib,
                    CCompiler = configuration.CCompiler.Path,
                    CppCompiler = configuration.CppCompiler.Path,
                    Linker = configuration.Linker?.Path,
                },
                run.SmokeTests,
            }),
        };
        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, s_jsonOptions));
    }
}
