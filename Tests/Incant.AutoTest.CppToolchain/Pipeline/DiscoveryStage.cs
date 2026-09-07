using Incant.Core.Cpp;
using Sdk = Incant.Core.Cpp.FindSdk.Sdk;
using SdkDiscoveryResult = Incant.Core.Cpp.FindSdk.DiscoveryResult;
using SdkFinder = Incant.Core.Cpp.FindSdk.Finder;
using SdkKind = Incant.Core.Cpp.FindSdk.Kind;
using SdkQuery = Incant.Core.Cpp.FindSdk.SdkQuery;
using ToolDiscoveryResult = Incant.Core.Cpp.FindTools.DiscoveryResult;
using ToolFinder = Incant.Core.Cpp.FindTools.Finder;
using ToolKind = Incant.Core.Cpp.FindTools.Kind;
using ToolSet = Incant.Core.Cpp.FindTools.ToolSet;
using ToolSetQuery = Incant.Core.Cpp.FindTools.ToolSetQuery;

namespace Incant.AutoTest.CppToolchain;

internal static class DiscoveryStage
{
    internal static async Task<bool> ExecuteAsync(AutoTestContext context, CancellationToken cancellationToken)
    {
        var toolFinder = ToolFinder.CreateDefault();
        var sdkFinder = SdkFinder.CreateDefault();
        DiscoveryProbe tools = await RunToolProbeAsync(context, "automatic/toolsets", "automatic",
            toolFinder, new ToolSetQuery { IncludePreview = true, Environment = context.BaseEnvironment },
            cancellationToken).ConfigureAwait(false);
        DiscoveryProbe sdks = await RunSdkProbeAsync(context, "automatic/sdks", "automatic",
            sdkFinder, new SdkQuery { IncludePreview = true, Environment = context.BaseEnvironment },
            cancellationToken).ConfigureAwait(false);

        foreach (InstallationRequirement requirement in context.Profile.Installations)
        {
            InstallationManifest? manifest = context.Manifest!.Installations
                .SingleOrDefault(item => item.Id == requirement.Id);
            var discovery = new InstallationDiscovery(requirement, manifest
                ?? InstallationIdentity.Ambient(requirement.Id, requirement.Kind, string.Empty, string.Empty));
            context.Installations.Add(discovery);
            if (manifest is null || manifest.Kind != requirement.Kind
                || string.IsNullOrWhiteSpace(manifest.RootPath) || !Path.IsPathFullyQualified(manifest.RootPath)
                || !File.Exists(manifest.RootPath) && !Directory.Exists(manifest.RootPath))
            {
                discovery.Failures.Add("The declared installation is absent or its root/kind is unusable.");
                continue;
            }

            IReadOnlyDictionary<string, string?> environment = context.EnvironmentFor(manifest);
            if (GetToolKind(requirement.Kind) is ToolKind kind)
            {
                var query = new ToolSetQuery
                {
                    Kind = kind,
                    RootPath = manifest.RootPath,
                    IncludePreview = true,
                    Environment = environment,
                };
                query = requirement.ToolVersion?.Apply(query) ?? query;
                DiscoveryProbe probe = await RunToolProbeAsync(context, requirement.Id + "/toolsets",
                    "managed installation", toolFinder, query, cancellationToken).ConfigureAwait(false);
                discovery.ToolSets.AddRange(probe.ToolSets);
                if (probe.ToolSets.Count == 0)
                {
                    discovery.Failures.Add(probe.Error ?? "No ToolSet satisfies the declared installation range.");
                }
            }

            foreach (SdkKind sdkKind in GetSdkKinds(requirement.Kind))
            {
                var query = new SdkQuery
                {
                    Kind = sdkKind,
                    RootPath = manifest.RootPath,
                    IncludePreview = true,
                    Environment = environment,
                };
                query = requirement.SdkVersion?.Apply(query) ?? query;
                DiscoveryProbe probe = await RunSdkProbeAsync(context, requirement.Id + "/sdk/" + sdkKind,
                    "managed installation", sdkFinder, query, cancellationToken).ConfigureAwait(false);
                discovery.Sdks.AddRange(probe.Sdks);
                if (probe.Error is not null)
                {
                    discovery.Decisions.Add(probe.Error);
                }
            }
        }

        foreach (ToolSet toolSet in tools.ToolSets.DistinctBy(InstallationIdentity.ToolSetKey))
        {
            InstallationKind? kind = toolSet.Kind switch
            {
                ToolKind.VisualStudio => InstallationKind.VisualStudio,
                ToolKind.Gnu => InstallationKind.Gnu,
                ToolKind.Llvm => InstallationKind.Llvm,
                ToolKind.Xcode => InstallationKind.Xcode,
                _ => null,
            };
            if (kind is null || !context.Profile.Definition.RequiredHostFamilies.Contains(kind.Value)
                || context.Installations.Any(owner => owner.ToolSets.Any(managed =>
                    InstallationIdentity.ToolSetKey(managed) == InstallationIdentity.ToolSetKey(toolSet))))
            {
                continue;
            }

            string id = "ambient-" + kind + "-" + InstallationIdentity.ShortId(InstallationIdentity.ToolSetKey(toolSet));
            string root = kind == InstallationKind.Xcode ? toolSet.EnvironmentPath
                : kind is InstallationKind.Gnu or InstallationKind.Llvm
                    ? toolSet.CompilerPath ?? toolSet.RootPath : toolSet.RootPath;
            var owner = new InstallationDiscovery(new InstallationRequirement(id, kind.Value, null, null, false),
                InstallationIdentity.Ambient(id, kind.Value, root, toolSet.Version?.ToString() ?? "unknown",
                    kind == InstallationKind.Xcode ? toolSet.EnvironmentPath : null), managed: false);
            owner.ToolSets.Add(toolSet);
            owner.Sdks.AddRange(sdks.Sdks.Where(sdk => GetSdkKinds(kind.Value).Contains(sdk.Kind)
                && (kind == InstallationKind.Xcode
                    ? PathIdentity.AreEqual(toolSet.EnvironmentPath, sdk.EnvironmentPath)
                    : kind == InstallationKind.VisualStudio
                        ? ToolchainResolution.MsvcIdentityMatches(toolSet, sdk)
                        : ToolchainResolution.CompilerMatches(toolSet, sdk))));
            context.Installations.Add(owner);
        }

        if (context.Profile.Definition.RequiredHostFamilies.Contains(InstallationKind.WindowsSdk))
        {
            foreach (Sdk sdk in sdks.Sdks.Where(sdk => sdk.Kind == SdkKind.Windows)
                .DistinctBy(InstallationIdentity.SdkKey))
            {
                string id = "ambient-windows-sdk-" + InstallationIdentity.ShortId(InstallationIdentity.SdkKey(sdk));
                var owner = new InstallationDiscovery(
                    new InstallationRequirement(id, InstallationKind.WindowsSdk, null, null, false),
                    InstallationIdentity.Ambient(id, InstallationKind.WindowsSdk, sdk.RootPath,
                        sdk.Version?.ToString() ?? "unknown"), managed: false);
                owner.Sdks.Add(sdk);
                context.Installations.Add(owner);
            }
        }

        return context.Installations.Where(owner => owner.Managed && owner.Requirement.Required)
            .All(owner => owner.Succeeded);
    }

    internal static async Task<DiscoveryProbe> RunSdkProbeAsync(
        AutoTestContext context,
        string name,
        string queryDescription,
        SdkFinder finder,
        SdkQuery query,
        CancellationToken cancellationToken)
    {
        string key = System.Text.Json.JsonSerializer.Serialize(query);
        if (context.SdkQueries.TryGetValue(key, out DiscoveryProbe? cached))
        {
            return cached;
        }

        var probe = new DiscoveryProbe
        {
            Name = name,
            Subject = DiscoverySubject.Sdks,
            Query = queryDescription,
        };
        context.DiscoveryProbes.Add(probe);
        try
        {
            SdkDiscoveryResult result = await finder.FindSdksAsync(
                query, cancellationToken).ConfigureAwait(false);
            probe.Succeeded = true;
            probe.Sdks = result.Sdks;
            context.SdkQueries.Add(key, probe);
            probe.Diagnostics = result.Diagnostics;
            context.AddDiagnostics(result.Diagnostics);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            probe.Error = exception.ToString();
        }

        return probe;
    }

    private static async Task<DiscoveryProbe> RunToolProbeAsync(
        AutoTestContext context,
        string name,
        string queryDescription,
        ToolFinder finder,
        ToolSetQuery query,
        CancellationToken cancellationToken)
    {
        var probe = new DiscoveryProbe
        {
            Name = name,
            Subject = DiscoverySubject.ToolSets,
            Query = queryDescription,
        };
        context.DiscoveryProbes.Add(probe);
        try
        {
            ToolDiscoveryResult result = await finder.FindToolSetsAsync(
                query, cancellationToken).ConfigureAwait(false);
            probe.Succeeded = true;
            probe.ToolSets = result.ToolSets;
            probe.Diagnostics = result.Diagnostics;
            context.AddDiagnostics(result.Diagnostics);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            probe.Error = exception.ToString();
        }

        return probe;
    }

    private static ToolKind? GetToolKind(InstallationKind kind) => kind switch
    {
        InstallationKind.VisualStudio => ToolKind.VisualStudio,
        InstallationKind.Gnu => ToolKind.Gnu,
        InstallationKind.Llvm => ToolKind.Llvm,
        InstallationKind.Xcode => ToolKind.Xcode,
        InstallationKind.AndroidNdk => ToolKind.AndroidNdk,
        InstallationKind.Emscripten => ToolKind.Emscripten,
        InstallationKind.WasiSdk => ToolKind.WasiSdk,
        InstallationKind.WindowsSdk => null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static IReadOnlyList<SdkKind> GetSdkKinds(InstallationKind kind) => kind switch
    {
        InstallationKind.VisualStudio => [SdkKind.Msvc],
        InstallationKind.WindowsSdk => [SdkKind.Windows],
        InstallationKind.Gnu => [SdkKind.Gnu],
        InstallationKind.Llvm => [SdkKind.Llvm],
        InstallationKind.Xcode => [SdkKind.AppleClang, SdkKind.Apple],
        InstallationKind.AndroidNdk => [SdkKind.AndroidNdk],
        InstallationKind.Emscripten => [SdkKind.Emscripten],
        InstallationKind.WasiSdk => [SdkKind.WasiSdk],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
