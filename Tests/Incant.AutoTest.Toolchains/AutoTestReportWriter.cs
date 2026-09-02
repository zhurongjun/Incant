using System.Text.Json;
using System.Text.Json.Serialization;
using Incant.Core.Toolchains;

/// <summary>Writes the complete machine-readable AutoTest report consumed as a CI artifact.</summary>
internal static class AutoTestReportWriter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes one report without changing the discovery or verification outcome.</summary>
    internal static void Write(
        string path,
        AutoTestCommand command,
        IReadOnlyCollection<AutoTestRun> runs,
        bool success,
        string? error)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var report = new
        {
            Success = success,
            Error = error,
            Command = command.Operation.ToString(),
            Kind = command.Kind?.ToString(),
            Target = command.Target?.ToString(),
            Architecture = command.Architecture?.ToString(),
            ClangClLinker = command.ClangClLinker?.ToString(),
            command.MsvcMajor,
            RequiredComponents = command.RequiredComponents.Select(component => component.ToString()),
            Runs = runs.Select(run => new
            {
                run.Name,
                Installations = run.Catalog.Installations.Select(CreateInstallationReport),
                Sdks = run.Catalog.Sdks.Select(CreateSdkReport),
                Profiles = run.Catalog.Profiles.Select(profile => new
                {
                    Kind = profile.Installation.Kind.ToString(),
                    SdkKind = profile.Sdk?.Kind.ToString(),
                    TargetPlatform = profile.TargetPlatform.ToString(),
                    TargetArchitecture = profile.TargetArchitecture.ToString(),
                    profile.TargetTriple,
                }),
                Diagnostics = run.Catalog.Diagnostics,
                SmokeTests = run.SmokeTests,
            }),
        };
        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, s_jsonOptions));
    }

    private static object CreateInstallationReport(Installation installation) => new
    {
        Kind = installation.Kind.ToString(),
        CompilerFamily = installation.CompilerFamily.ToString(),
        installation.RootPath,
        HostOS = installation.HostOS.ToString(),
        HostArchitecture = installation.HostArchitecture.ToString(),
        ProductVersion = installation.ProductVersion?.ToString(),
        CompilerVersion = installation.CompilerVersion?.ToString(),
        Channel = installation.Channel.ToString(),
        Sources = installation.Sources.Select(source => source.ToString()),
        TargetPlatforms = installation.TargetPlatforms.Select(platform => platform.ToString()),
        TargetArchitectures = installation.TargetArchitectures.Select(architecture => architecture.ToString()),
        Components = installation.Components.Select(component => new
        {
            Kind = component.Kind.ToString(),
            component.Path,
            HostArchitecture = component.HostArchitecture.ToString(),
            TargetArchitecture = component.TargetArchitecture.ToString(),
        }),
        installation.Diagnostics,
    };

    private static object CreateSdkReport(SdkInstallation sdk) => new
    {
        Kind = sdk.Kind.ToString(),
        TargetPlatform = sdk.TargetPlatform.ToString(),
        sdk.RootPath,
        sdk.SysrootPath,
        Version = sdk.Version?.ToString(),
        Sources = sdk.Sources.Select(source => source.ToString()),
        TargetArchitectures = sdk.TargetArchitectures.Select(architecture => architecture.ToString()),
        sdk.SupportedApiLevels,
        sdk.Diagnostics,
    };
}
