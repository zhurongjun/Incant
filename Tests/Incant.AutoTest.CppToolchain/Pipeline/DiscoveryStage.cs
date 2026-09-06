using Incant.Core.Cpp;
using Resource = Incant.Core.Cpp.FindSdk.Resource;
using Sdk = Incant.Core.Cpp.FindSdk.Sdk;
using SdkDiscoveryResult = Incant.Core.Cpp.FindSdk.DiscoveryResult;
using SdkFinder = Incant.Core.Cpp.FindSdk.Finder;
using SdkKind = Incant.Core.Cpp.FindSdk.Kind;
using SdkQuery = Incant.Core.Cpp.FindSdk.SdkQuery;
using TargetLayout = Incant.Core.Cpp.FindSdk.TargetLayout;
using ToolDiscoveryResult = Incant.Core.Cpp.FindTools.DiscoveryResult;
using ToolFinder = Incant.Core.Cpp.FindTools.Finder;
using ToolKind = Incant.Core.Cpp.FindTools.Kind;
using ToolSet = Incant.Core.Cpp.FindTools.ToolSet;
using ToolSetQuery = Incant.Core.Cpp.FindTools.ToolSetQuery;

namespace Incant.AutoTest.CppToolchain;

internal static class DiscoveryStage
{
    internal static async Task<bool> ExecuteAsync(
        AutoTestContext context,
        CancellationToken cancellationToken)
    {
        var toolFinder = ToolFinder.CreateDefault();
        var sdkFinder = SdkFinder.CreateDefault();
        DiscoveryProbe allToolSets = await RunToolProbeAsync(
            context,
            "all/toolsets",
            "unconstrained",
            toolFinder,
            new ToolSetQuery
            {
                IncludePreview = true,
                Environment = context.BaseEnvironment,
            },
            cancellationToken).ConfigureAwait(false);
        DiscoveryProbe allSdks = await RunSdkProbeAsync(
            context,
            "all/sdks",
            "unconstrained",
            sdkFinder,
            new SdkQuery
            {
                IncludePreview = true,
                Environment = context.BaseEnvironment,
            },
            cancellationToken).ConfigureAwait(false);

        foreach (InstallationRequirement requirement in context.Profile.Installations)
        {
            InstallationManifest manifest = context.Manifest!.Installations
                .Single(installation => installation.Id == requirement.Id);
            var discovery = new InstallationDiscovery(requirement, manifest);
            context.Installations.Add(discovery);
            await DiscoverInstallationAsync(
                context, discovery, toolFinder, sdkFinder, cancellationToken).ConfigureAwait(false);
        }

        return allToolSets.Completed
            && allSdks.Completed
            && context.Installations
            .Where(installation => installation.Requirement.Required)
            .All(installation => installation.Succeeded);
    }

    private static async Task DiscoverInstallationAsync(
        AutoTestContext context,
        InstallationDiscovery discovery,
        ToolFinder toolFinder,
        SdkFinder sdkFinder,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string?> environment =
            context.EnvironmentFor(discovery.Manifest);
        ToolKind? toolKind = GetToolKind(discovery.Requirement.Kind);
        if (toolKind is ToolKind concreteToolKind)
        {
            await DiscoverToolSetsAsync(
                context,
                discovery,
                toolFinder,
                concreteToolKind,
                environment,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (SdkKind sdkKind in GetSdkKinds(discovery.Requirement.Kind))
        {
            await DiscoverSdksAsync(
                context,
                discovery,
                sdkFinder,
                sdkKind,
                environment,
                cancellationToken).ConfigureAwait(false);
        }

        if (toolKind is null && discovery.Sdks.Count == 0)
        {
            discovery.Failures.Add("No SDK matched the declared installation.");
        }
        else if (toolKind is not null && discovery.ToolSets.Count == 0)
        {
            discovery.Failures.Add("No ToolSet matched the declared installation.");
        }
    }

    private static async Task DiscoverToolSetsAsync(
        AutoTestContext context,
        InstallationDiscovery discovery,
        ToolFinder finder,
        ToolKind kind,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        string prefix = discovery.Requirement.Id + "/toolsets";
        var kindQuery = new ToolSetQuery
        {
            Kind = kind,
            IncludePreview = true,
            Environment = environment,
        };
        DiscoveryProbe automaticProbe = await RunToolProbeAsync(
            context,
            prefix + "/automatic",
            $"kind {kind} with the installation environment",
            finder,
            kindQuery,
            cancellationToken).ConfigureAwait(false);

        VersionRule manifestVersion = ExactManifestVersion(
            discovery.Requirement.ToolVersion, discovery.Manifest.Version);
        ToolSetQuery versionQuery = manifestVersion.Apply(kindQuery);
        DiscoveryProbe versionProbe = await RunToolProbeAsync(
            context,
            prefix + "/version",
            Describe(manifestVersion),
            finder,
            versionQuery,
            cancellationToken).ConfigureAwait(false);

        ToolSetQuery explicitQuery = versionQuery with
        {
            RootPath = discovery.Manifest.RootPath,
        };
        DiscoveryProbe explicitProbe = await RunToolProbeAsync(
            context,
            prefix + "/explicit",
            discovery.Manifest.RootPath,
            finder,
            explicitQuery,
            cancellationToken).ConfigureAwait(false);

        ToolSet[] automaticMatches = automaticProbe.ToolSets
            .Where(toolSet => toolSet.Kind == kind
                && Related(discovery.Manifest.RootPath, toolSet)
                && manifestVersion.Matches(toolSet)
                && (discovery.Requirement.ToolVersion is null
                    || discovery.Requirement.ToolVersion.Matches(toolSet)))
            .ToArray();
        ToolSet[] versionMatches = versionProbe.ToolSets
            .Where(toolSet => Related(discovery.Manifest.RootPath, toolSet)
                && (discovery.Requirement.ToolVersion is null
                    || discovery.Requirement.ToolVersion.Matches(toolSet)))
            .ToArray();
        ToolSet[] explicitMatches = explicitProbe.ToolSets.ToArray();
        discovery.ToolSets.AddRange(explicitMatches);
        CompareIdentities(
            automaticMatches.Select(ToolIdentity),
            explicitMatches.Select(ToolIdentity),
            discovery,
            "ToolSet automatic query");
        CompareIdentities(
            versionMatches.Select(ToolIdentity),
            explicitMatches.Select(ToolIdentity),
            discovery,
            "ToolSet version query");

        string missingPath = Path.Combine(
            context.Options.WorkRoot, "__missing-explicit-" + Guid.NewGuid().ToString("N"));
        DiscoveryProbe missingPathProbe = await ExpectToolDiscoveryExceptionAsync(
            context,
            prefix + "/missing-path",
            finder,
            kindQuery with { RootPath = missingPath },
            cancellationToken).ConfigureAwait(false);
        ToolSetQuery impossibleVersionQuery = ExactManifestVersion(
            discovery.Requirement.ToolVersion, "9999.0").Apply(kindQuery) with
        {
            RootPath = discovery.Manifest.RootPath,
        };
        DiscoveryProbe wrongVersion = await RunToolProbeAsync(
            context,
            prefix + "/wrong-version",
            "explicit root with version 9999.0",
            finder,
            impossibleVersionQuery,
            cancellationToken).ConfigureAwait(false);
        if (wrongVersion.ToolSets.Count != 0)
        {
            discovery.Failures.Add("The explicit ToolSet root matched an impossible version.");
        }

        if (!automaticProbe.Completed
            || !versionProbe.Completed
            || !explicitProbe.Succeeded
            || !missingPathProbe.Succeeded
            || !wrongVersion.Succeeded)
        {
            discovery.Failures.Add("One or more ToolSet discovery modes failed.");
        }
    }

    private static async Task DiscoverSdksAsync(
        AutoTestContext context,
        InstallationDiscovery discovery,
        SdkFinder finder,
        SdkKind kind,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        string prefix = discovery.Requirement.Id + "/sdks/" + kind;
        var kindQuery = new SdkQuery
        {
            Kind = kind,
            IncludePreview = true,
            Environment = environment,
        };
        DiscoveryProbe automaticProbe = await RunSdkProbeAsync(
            context,
            prefix + "/automatic",
            $"kind {kind} with the installation environment",
            finder,
            kindQuery,
            cancellationToken).ConfigureAwait(false);

        VersionRule manifestVersion = ExactManifestVersion(
            discovery.Requirement.SdkVersion, discovery.Manifest.Version);
        SdkQuery versionQuery = manifestVersion.Apply(kindQuery);
        DiscoveryProbe versionProbe = await RunSdkProbeAsync(
            context,
            prefix + "/version",
            Describe(manifestVersion),
            finder,
            versionQuery,
            cancellationToken).ConfigureAwait(false);

        SdkQuery explicitQuery = versionQuery with
        {
            RootPath = discovery.Manifest.RootPath,
        };
        DiscoveryProbe explicitProbe = await RunSdkProbeAsync(
            context,
            prefix + "/explicit",
            discovery.Manifest.RootPath,
            finder,
            explicitQuery,
            cancellationToken).ConfigureAwait(false);

        Sdk[] automaticMatches = automaticProbe.Sdks
            .Where(sdk => sdk.Kind == kind
                && Related(discovery.Manifest.RootPath, sdk)
                && SdkVersionMatches(manifestVersion, sdk)
                && SdkVersionMatches(discovery.Requirement.SdkVersion, sdk))
            .ToArray();
        Sdk[] versionMatches = versionProbe.Sdks
            .Where(sdk => Related(discovery.Manifest.RootPath, sdk)
                && SdkVersionMatches(discovery.Requirement.SdkVersion, sdk))
            .ToArray();
        Sdk[] explicitMatches = explicitProbe.Sdks.ToArray();
        discovery.Sdks.AddRange(explicitMatches);
        if (explicitMatches.Length == 0)
        {
            discovery.Failures.Add(
                $"No {kind} SDK matched the declared installation and exact version.");
        }
        CompareIdentities(
            automaticMatches.Select(SdkIdentity),
            explicitMatches.Select(SdkIdentity),
            discovery,
            $"SDK {kind} automatic query");
        CompareIdentities(
            versionMatches.Select(SdkIdentity),
            explicitMatches.Select(SdkIdentity),
            discovery,
            $"SDK {kind} version query");

        string missingPath = Path.Combine(
            context.Options.WorkRoot, "__missing-explicit-" + Guid.NewGuid().ToString("N"));
        DiscoveryProbe missingPathProbe = await ExpectSdkDiscoveryExceptionAsync(
            context,
            prefix + "/missing-path",
            finder,
            kindQuery with { RootPath = missingPath },
            cancellationToken).ConfigureAwait(false);
        SdkQuery impossibleVersionQuery = ExactManifestVersion(
            discovery.Requirement.SdkVersion, "9999.0").Apply(kindQuery) with
        {
            RootPath = discovery.Manifest.RootPath,
        };
        DiscoveryProbe wrongVersion = await RunSdkProbeAsync(
            context,
            prefix + "/wrong-version",
            "explicit root with version 9999.0",
            finder,
            impossibleVersionQuery,
            cancellationToken).ConfigureAwait(false);
        if (wrongVersion.Sdks.Count != 0)
        {
            discovery.Failures.Add($"The explicit {kind} SDK root matched an impossible version.");
        }

        DiscoveryProbe wrongTarget = await RunSdkProbeAsync(
            context,
            prefix + "/wrong-target",
            "explicit root with an incompatible target",
            finder,
            explicitQuery with { TargetPlatform = IncompatibleTarget(kind) },
            cancellationToken).ConfigureAwait(false);
        if (wrongTarget.Sdks.Count != 0)
        {
            discovery.Failures.Add($"The explicit {kind} SDK root matched an incompatible target.");
        }

        if (!automaticProbe.Completed
            || !versionProbe.Completed
            || !explicitProbe.Succeeded
            || !missingPathProbe.Succeeded
            || !wrongVersion.Succeeded
            || !wrongTarget.Succeeded)
        {
            discovery.Failures.Add($"One or more {kind} SDK discovery modes failed.");
        }
    }

    internal static async Task<DiscoveryProbe> RunSdkProbeAsync(
        AutoTestContext context,
        string name,
        string queryDescription,
        SdkFinder finder,
        SdkQuery query,
        CancellationToken cancellationToken)
    {
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
            probe.Succeeded = result.Diagnostics.All(
                diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
            probe.Sdks = result.Sdks;
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
            probe.Succeeded = result.Diagnostics.All(
                diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
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

    private static async Task<DiscoveryProbe> ExpectToolDiscoveryExceptionAsync(
        AutoTestContext context,
        string name,
        ToolFinder finder,
        ToolSetQuery query,
        CancellationToken cancellationToken)
    {
        var probe = new DiscoveryProbe
        {
            Name = name,
            Subject = DiscoverySubject.ToolSets,
            Query = query.RootPath!,
            ExpectedFailure = true,
        };
        context.DiscoveryProbes.Add(probe);
        try
        {
            await finder.FindToolSetsAsync(query, cancellationToken).ConfigureAwait(false);
            probe.Error = "Discovery did not reject the nonexistent explicit path.";
        }
        catch (DiscoveryException exception)
        {
            probe.Succeeded = true;
            probe.Diagnostics = exception.Diagnostics;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            probe.Error = exception.ToString();
        }

        return probe;
    }

    private static async Task<DiscoveryProbe> ExpectSdkDiscoveryExceptionAsync(
        AutoTestContext context,
        string name,
        SdkFinder finder,
        SdkQuery query,
        CancellationToken cancellationToken)
    {
        var probe = new DiscoveryProbe
        {
            Name = name,
            Subject = DiscoverySubject.Sdks,
            Query = query.RootPath!,
            ExpectedFailure = true,
        };
        context.DiscoveryProbes.Add(probe);
        try
        {
            await finder.FindSdksAsync(query, cancellationToken).ConfigureAwait(false);
            probe.Error = "Discovery did not reject the nonexistent explicit path.";
        }
        catch (DiscoveryException exception)
        {
            probe.Succeeded = true;
            probe.Diagnostics = exception.Diagnostics;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            probe.Error = exception.ToString();
        }

        return probe;
    }

    private static void CompareIdentities(
        IEnumerable<string> broadResults,
        IEnumerable<string> explicitResults,
        InstallationDiscovery discovery,
        string subject)
    {
        string[] broadKeys = broadResults
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        string[] explicitKeys = explicitResults
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        int missingCount = explicitKeys.Except(
            broadKeys, StringComparer.Ordinal).Count();
        if (missingCount > 0)
        {
            discovery.Failures.Add(
                $"{subject} omitted {missingCount} identity or identities confirmed "
                + "by explicit-root discovery.");
            return;
        }

        int additionalCount = broadKeys.Except(
            explicitKeys, StringComparer.Ordinal).Count();
        discovery.Decisions.Add(
            $"{subject} contains all {explicitKeys.Length} explicit-root identities; "
            + $"additional identities: {additionalCount}.");
    }

    private static bool SdkVersionMatches(VersionRule? rule, Sdk sdk)
    {
        if (rule is null)
        {
            return true;
        }

        Version? actual = rule.Source == VersionSource.ProductVersion
            ? sdk.ProductVersion
            : sdk.Version;
        return rule.Constraint.Matches(actual);
    }

    private static string ToolIdentity(ToolSet toolSet) => string.Join(
        "|",
        toolSet.Kind,
        PathIdentity.Normalize(toolSet.RootPath),
        PathIdentity.Normalize(toolSet.EnvironmentPath),
        toolSet.CompilerPath is null ? string.Empty : PathIdentity.Normalize(toolSet.CompilerPath),
        toolSet.Version,
        toolSet.ProductVersion,
        toolSet.CompilerVersion,
        CanonicalTriple(toolSet.DefaultTargetTriple),
        toolSet.Channel);

    private static string SdkIdentity(Sdk sdk) =>
        string.Join(
            "|",
            sdk.Kind,
            PathIdentity.Normalize(sdk.RootPath),
            PathIdentity.Normalize(sdk.EnvironmentPath),
            sdk.CompilerPath is null ? string.Empty : PathIdentity.Normalize(sdk.CompilerPath),
            sdk.Version,
            sdk.ProductVersion,
            sdk.Channel,
            string.Join(",", sdk.Layouts
                .Select(LayoutIdentity)
                .OrderBy(identity => identity, StringComparer.Ordinal)));

    private static string LayoutIdentity(TargetLayout layout) => string.Join(
        "/",
        layout.Platform,
        layout.Architecture,
        CanonicalTriple(layout.TargetTriple),
        layout.SysrootPath is null ? string.Empty : PathIdentity.Normalize(layout.SysrootPath),
        layout.Multilib,
        layout.MinimumDeploymentVersion,
        layout.DefaultDeploymentVersion,
        string.Join(",", layout.ApiLevels),
        string.Join(",", layout.ApiAliases.OrderBy(alias => alias.Key)
            .Select(alias => $"{alias.Key}:{alias.Value}")),
        string.Join(";", layout.Resources.Select(ResourceIdentity)));

    private static string ResourceIdentity(Resource resource) => string.Join(
        ":",
        resource.Purpose,
        PathIdentity.Normalize(resource.Path),
        resource.IsExternal,
        resource.ApiLevel,
        resource.IsDirectory);

    private static string CanonicalTriple(string? triple) =>
        string.IsNullOrWhiteSpace(triple)
            ? string.Empty
            : TargetTripleIdentity.Canonicalize(triple);

    private static bool Related(string root, ToolSet toolSet) =>
        Related(root, toolSet.RootPath)
        || Related(root, toolSet.EnvironmentPath)
        || toolSet.CompilerPath is not null && Related(root, toolSet.CompilerPath);

    private static bool Related(string root, Sdk sdk) =>
        Related(root, sdk.RootPath)
        || Related(root, sdk.EnvironmentPath)
        || sdk.CompilerPath is not null && Related(root, sdk.CompilerPath);

    private static bool Related(string left, string right) =>
        PathIdentity.Related(left, right);

    private static VersionRule ExactManifestVersion(VersionRule? profileRule, string value) =>
        new(
            value,
            profileRule?.Source ?? VersionSource.Version,
            VersionPrecision.Exact);

    private static string Describe(VersionRule? rule) =>
        rule is null ? "no version constraint" : $"{rule.Source} {rule.Precision} {rule.Value}";

    private static TargetPlatform IncompatibleTarget(SdkKind kind) => kind switch
    {
        SdkKind.Windows or SdkKind.Msvc => TargetPlatform.Wasi,
        SdkKind.Apple or SdkKind.AppleClang => TargetPlatform.Linux,
        SdkKind.Gnu or SdkKind.Llvm or SdkKind.Linux or SdkKind.Sysroot => TargetPlatform.Wasi,
        SdkKind.AndroidNdk or SdkKind.Emscripten or SdkKind.WasiSdk => TargetPlatform.Windows,
        _ => TargetPlatform.Unknown,
    };

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
