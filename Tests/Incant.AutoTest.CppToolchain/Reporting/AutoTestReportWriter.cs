using System.Text.Json;
using System.Text.Json.Serialization;
using Incant.Core.Cpp;
using Incant.Core.Cpp.FindSdk;
using Incant.Core.Cpp.FindTools;

namespace Incant.AutoTest.CppToolchain;

internal static class AutoTestReportWriter
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static async Task WriteAsync(
        AutoTestContext context,
        CancellationToken cancellationToken)
    {
        string path = context.Options.ReportPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ToolSet[] toolSets = context.DiscoveryProbes.SelectMany(probe => probe.ToolSets)
            .Concat(context.Installations.SelectMany(owner => owner.ToolSets))
            .Concat(context.Candidates.Select(candidate => candidate.Toolchain).OfType<ResolvedToolchain>()
                .SelectMany(toolchain => toolchain.AuxiliaryToolSet is null
                    ? new[] { toolchain.ToolSet } : new[] { toolchain.ToolSet, toolchain.AuxiliaryToolSet })).ToArray();
        Sdk[] sdks = context.DiscoveryProbes.SelectMany(probe => probe.Sdks)
            .Concat(context.Installations.SelectMany(owner => owner.Sdks))
            .Concat(context.Candidates.Select(candidate => candidate.Toolchain).OfType<ResolvedToolchain>()
                .SelectMany(toolchain => toolchain.Sdks.Select(component => component.Sdk))).ToArray();
        object report = new
        {
            SchemaVersion = 2,
            Profile = new
            {
                context.Profile.Name,
                context.Profile.Description,
                context.Profile.RunnerImage,
                context.Profile.Definition.RequiredHostFamilies,
                context.Profile.HostOS,
                context.Profile.HostArchitecture,
                HostExecutionArchitectures =
                    context.HostCapabilities.Architectures,
                context.Profile.ExecutionCapabilities,
                Installations = context.Profile.Installations.Select(requirement => new
                {
                    requirement.Id,
                    requirement.Kind,
                    ToolVersion = VersionRuleSnapshot(requirement.ToolVersion),
                    SdkVersion = VersionRuleSnapshot(requirement.SdkVersion),
                    requirement.Required,
                }),
                Targets = new
                {
                    context.Profile.NativeArchitectures,
                    context.Profile.TestAllGnuMultilibs,
                    context.Profile.WindowsMsvcArchitectures,
                    context.Profile.WindowsLlvmArchitectures,
                    context.Profile.ApplePlatforms,
                    context.Profile.AppleArchitectures,
                    context.Profile.AndroidArchitectures,
                    context.Profile.AndroidApi,
                    context.Profile.EmscriptenMultilibs,
                    context.Profile.WasiTargetTriples,
                },
            },
            context.StartedAt,
            context.CompletedAt,
            Success = context.ExitCode == 0,
            context.ExitCode,
            Error = context.FatalError,
            Invocation = new
            {
                Environment = context.Options.EnvironmentPath,
                Report = context.Options.ReportPath,
                WorkRoot = context.Options.WorkRoot,
                context.Options.KeepWork,
            },
            Environment = context.Manifest,
            Pipeline = context.Stages.Select(stage => new
            {
                stage.Name,
                stage.Status,
                ElapsedMilliseconds = stage.Elapsed.TotalMilliseconds,
                stage.Messages,
            }),
            Discovery = context.DiscoveryProbes.Select(probe => new
            {
                probe.Name,
                probe.Subject,
                probe.Query,
                probe.Completed,
                probe.Succeeded,
                probe.Error,
                ToolSets = probe.ToolSets.Select(ToolSetId),
                Sdks = probe.Sdks.Select(SdkId),
                Diagnostics = probe.Diagnostics.Select(DiagnosticSnapshot),
            }),
            Installations = context.Installations.Select(installation => new
            {
                installation.Requirement.Id,
                installation.Requirement.Kind,
                Origin = installation.Managed ? "managed" : "preinstalled",
                ManifestRoot = installation.Manifest.RootPath,
                installation.Manifest.Version,
                installation.Succeeded,
                installation.Decisions,
                installation.Failures,
                ToolSets = installation.ToolSets.Select(ToolSetId),
                Sdks = installation.Sdks.Select(SdkId),
            }),
            Diagnostics = context.Diagnostics.Select(DiagnosticSnapshot),
            Snapshots = new
            {
                ToolSets = toolSets.DistinctBy(ToolSetId).Select(toolSet => new { Id = ToolSetId(toolSet), Snapshot = ToolSetSnapshot(toolSet) }),
                Sdks = sdks.DistinctBy(SdkId).Select(sdk => new { Id = SdkId(sdk), Snapshot = SdkSnapshot(sdk) }),
            },
            Coverage = context.Coverage.Select(result => new { result.Requirement, result.Passed, result.ScenarioIds, result.Reason }),
            Scenarios = context.Candidates.Select(CandidateSnapshot),
        };

        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream, report, s_options, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
        }
    }

    private static object? VersionRuleSnapshot(VersionRule? rule) => rule is null
        ? null
        : new
        {
            rule.Value,
            rule.Source,
            rule.Precision,
        };

    private static object CandidateSnapshot(ToolchainCandidate candidate) => new
    {
        candidate.Id,
        candidate.InstallationIds,
        candidate.Required,
        candidate.Status,
        candidate.Decisions,
        candidate.Failures,
        Toolchain = candidate.Toolchain is not ResolvedToolchain toolchain
            ? null
            : new
            {
                toolchain.AdapterKind,
                toolchain.LinkerFlavor,
                ToolSetId = ToolSetId(toolchain.ToolSet),
                AuxiliaryToolSet = toolchain.AuxiliaryToolSet is null
                    ? null
                    : ToolSetId(toolchain.AuxiliaryToolSet),
                Sdks = toolchain.Sdks.Select(component => new
                {
                    component.Role,
                    SdkId = SdkId(component.Sdk),
                    LayoutIndex = component.Sdk.Layouts.ToList().IndexOf(component.Layout),
                }),
                toolchain.TargetPlatform,
                toolchain.TargetArchitecture,
                toolchain.TargetTriple,
                toolchain.Multilib,
                toolchain.AndroidApi,
                CCompiler = ToolSnapshot(toolchain.CCompiler),
                CppCompiler = ToolSnapshot(toolchain.CppCompiler),
                Archiver = ToolSnapshot(toolchain.Archiver),
                Ranlib = toolchain.Ranlib is null
                    ? null
                    : ToolSnapshot(toolchain.Ranlib),
                Linker = toolchain.Linker is null ? null : ToolSnapshot(toolchain.Linker),
                toolchain.ExecutionMode,
                toolchain.RuntimePath,
            },
        BuildPlan = candidate.BuildPlan is null
            ? null
            : new
            {
                candidate.BuildPlan.WorkDirectory,
                Actions = candidate.BuildPlan.Actions.Select(action => new
                {
                    action.Id,
                    action.Phase,
                    action.ExecutablePath,
                    action.Arguments,
                    action.WorkingDirectory,
                    action.Dependencies,
                    action.ExpectedArtifacts,
                    action.ExpectedOutputFragments,
                    TimeoutMilliseconds = action.Timeout.TotalMilliseconds,
                }),
            },
        Results = candidate.Actions.Select(result => new
        {
            result.Id,
            result.Phase,
            result.ExecutablePath,
            result.Arguments,
            result.Status,
            result.ExitCode,
            result.TimedOut,
            ElapsedMilliseconds = result.Elapsed.TotalMilliseconds,
            result.StandardOutputLog,
            result.StandardErrorLog,
            result.Artifacts,
            result.Error,
        }),
    };

    private static string ToolSetId(ToolSet toolSet) =>
        "toolset-" + InstallationIdentity.ShortId(JsonSerializer.Serialize(ToolSetSnapshot(toolSet), s_options));

    private static string SdkId(Sdk sdk) =>
        "sdk-" + InstallationIdentity.ShortId(JsonSerializer.Serialize(SdkSnapshot(sdk), s_options));

    private static object ToolSetSnapshot(ToolSet toolSet) => new
    {
        toolSet.Kind,
        toolSet.RootPath,
        toolSet.EnvironmentPath,
        Version = toolSet.Version?.ToString(),
        ProductVersion = toolSet.ProductVersion?.ToString(),
        CompilerVersion = toolSet.CompilerVersion?.ToString(),
        toolSet.CompilerPath,
        toolSet.DefaultTargetTriple,
        toolSet.Channel,
        toolSet.HostOS,
        toolSet.Sources,
        Diagnostics = toolSet.Diagnostics.Select(DiagnosticSnapshot),
    };

    private static object ToolSnapshot(Tool tool) => new
    {
        tool.Name,
        tool.Path,
        tool.HostArchitecture,
        tool.TargetArchitecture,
    };

    private static object SdkSnapshot(Sdk sdk) => new
    {
        sdk.Kind,
        sdk.RootPath,
        sdk.EnvironmentPath,
        Version = sdk.Version?.ToString(),
        ProductVersion = sdk.ProductVersion?.ToString(),
        sdk.CompilerPath,
        sdk.Channel,
        sdk.Sources,
        Diagnostics = sdk.Diagnostics.Select(DiagnosticSnapshot),
        Layouts = sdk.Layouts.Select(LayoutSnapshot),
    };

    private static object LayoutSnapshot(TargetLayout layout) => new
    {
        layout.Platform,
        layout.Architecture,
        layout.TargetTriple,
        layout.SysrootPath,
        layout.Multilib,
        MinimumDeploymentVersion = layout.MinimumDeploymentVersion?.ToString(),
        DefaultDeploymentVersion = layout.DefaultDeploymentVersion?.ToString(),
        layout.ApiLevels,
        layout.ApiAliases,
        Diagnostics = layout.Diagnostics.Select(DiagnosticSnapshot),
        Resources = layout.Resources.Select(resource => new
        {
            resource.Purpose,
            resource.Path,
            resource.IsExternal,
            resource.ApiLevel,
            resource.IsDirectory,
        }),
    };

    private static object DiagnosticSnapshot(Diagnostic diagnostic) => new
    {
        diagnostic.Severity,
        diagnostic.Code,
        diagnostic.Provider,
        diagnostic.Message,
        diagnostic.Path,
    };
}
