using Incant.Base;
using Incant.Core.Cpp;
using static Incant.AutoTest.CppToolchain.ToolchainResolution;
using Sdk = Incant.Core.Cpp.FindSdk.Sdk;
using SdkFinder = Incant.Core.Cpp.FindSdk.Finder;
using SdkKind = Incant.Core.Cpp.FindSdk.Kind;
using SdkQuery = Incant.Core.Cpp.FindSdk.SdkQuery;
using TargetLayout = Incant.Core.Cpp.FindSdk.TargetLayout;
using Tool = Incant.Core.Cpp.FindTools.Tool;
using ToolKind = Incant.Core.Cpp.FindTools.Kind;
using ToolNames = Incant.Core.Cpp.FindTools.ToolNames;
using ToolQuery = Incant.Core.Cpp.FindTools.ToolQuery;
using ToolSet = Incant.Core.Cpp.FindTools.ToolSet;

namespace Incant.AutoTest.CppToolchain;

internal static class BundleToolchainResolver
{
    internal static Task ResolveAsync(
        AutoTestContext context,
        CancellationToken cancellationToken) =>
        ResolveBundlesAsync(context, cancellationToken);

    private static async Task ResolveBundlesAsync(
        AutoTestContext context,
        CancellationToken cancellationToken)
    {
        foreach (InstallationDiscovery owner in context.Installations.Where(
            installation => installation.Requirement.Kind is InstallationKind.AndroidNdk
                or InstallationKind.Emscripten
                or InstallationKind.WasiSdk))
        {
            (ToolKind toolKind, SdkKind sdkKind) = ExpectedKinds(
                owner.Requirement.Kind);
            ToolSet? toolSet = owner.ToolSets
                .Where(candidate => candidate.Kind == toolKind
                    && BelongsTo(owner, candidate))
                .OrderByDescending(candidate => candidate.Version)
                .FirstOrDefault();
            Sdk? sdk = owner.Sdks
                .Where(candidate => candidate.Kind == sdkKind
                    && BelongsTo(owner, candidate))
                .OrderByDescending(candidate => candidate.Version)
                .FirstOrDefault();
            if (toolSet is null || sdk is null)
            {
                AddInvalid(
                    context,
                    owner.Requirement.Id + "-bundle-identity",
                    [owner.Requirement.Id],
                    "The declared bundle root did not yield both its expected ToolSet and SDK kinds.");
                continue;
            }

            if (!Related(toolSet.RootPath, sdk.RootPath))
            {
                AddInvalid(
                    context,
                    owner.Requirement.Id + "-bundle-root-mismatch",
                    [owner.Requirement.Id],
                    "The bundle ToolSet and SDK do not belong to the same installation root.");
                continue;
            }

            if (owner.Requirement.Kind == InstallationKind.AndroidNdk)
            {
                foreach (TargetArchitecture architecture in
                    context.Profile.AndroidArchitectures.Concat(sdk.Layouts
                        .Where(layout => layout.Platform == TargetPlatform.Android)
                        .Select(layout => layout.Architecture))
                        .Where(architecture => architecture != TargetArchitecture.Unknown)
                        .Distinct())
                {
                    await AddBundleCandidateAsync(
                        context,
                        owner,
                        toolSet,
                        sdk,
                        TargetPlatform.Android,
                        architecture,
                        targetTriple: null,
                        multilib: null,
                        context.Profile.AndroidApi,
                        BuildAdapterKind.Android,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            else if (owner.Requirement.Kind == InstallationKind.Emscripten)
            {
                foreach (string multilib in context.Profile.EmscriptenMultilibs)
                {
                    await AddBundleCandidateAsync(
                        context,
                        owner,
                        toolSet,
                        sdk,
                        TargetPlatform.Emscripten,
                        TargetArchitecture.Wasm32,
                        toolSet.DefaultTargetTriple ?? "wasm32-unknown-emscripten",
                        multilib,
                        androidApi: null,
                        BuildAdapterKind.Emscripten,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                foreach (string triple in context.Profile.WasiTargetTriples)
                {
                    await AddBundleCandidateAsync(
                        context,
                        owner,
                        toolSet,
                        sdk,
                        TargetPlatform.Wasi,
                        TargetArchitecture.Wasm32,
                        triple,
                        multilib: null,
                        androidApi: null,
                        BuildAdapterKind.Wasi,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task AddBundleCandidateAsync(
        AutoTestContext context,
        InstallationDiscovery owner,
        ToolSet toolSet,
        Sdk discoveredSdk,
        TargetPlatform platform,
        TargetArchitecture architecture,
        string? targetTriple,
        string? multilib,
        int? androidApi,
        BuildAdapterKind adapter,
        CancellationToken cancellationToken)
    {
        string? discriminator = multilib
            ?? androidApi?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? (platform == TargetPlatform.Wasi
                ? targetTriple ?? string.Empty
                : null);
        string id = CreateId(
            owner.Requirement.Id,
            platform,
            architecture,
            discriminator);
        var candidate = new ToolchainCandidate(id, [owner.Requirement.Id],
            owner.Managed && (platform != TargetPlatform.Android
                || context.Profile.AndroidArchitectures.Contains(architecture)));
        context.Candidates.Add(candidate);

        var sdkQuery = new SdkQuery
        {
            Kind = discoveredSdk.Kind,
            RootPath = owner.Manifest.RootPath,
            TargetPlatform = platform,
            TargetArchitecture = architecture,
            TargetTriple = targetTriple,
            Multilib = multilib,
            AndroidApi = androidApi,
            IncludePreview = true,
            Environment = context.EnvironmentFor(owner.Manifest),
        };
        sdkQuery = owner.Requirement.SdkVersion?.Apply(sdkQuery) ?? sdkQuery;
        DiscoveryProbe targetProbe = await DiscoveryStage.RunSdkProbeAsync(
            context,
            $"{owner.Requirement.Id}/resolve/{platform}/{architecture}/{discriminator ?? targetTriple ?? "default"}",
            "explicit root with target, API, and multilib constraints",
            SdkFinder.CreateDefault(),
            sdkQuery,
            cancellationToken).ConfigureAwait(false);
        if (!targetProbe.Succeeded)
        {
            candidate.Invalidate(
                "The target-constrained bundle SDK query reported an error.");
            return;
        }

        Sdk? sdk = targetProbe.Sdks
            .Where(candidateSdk => candidateSdk.Kind == discoveredSdk.Kind
                && BelongsTo(owner, candidateSdk))
            .OrderByDescending(candidateSdk => candidateSdk.Version)
            .FirstOrDefault();
        TargetLayout? layout = sdk is null
            ? null
            : FindLayout(sdk, platform, architecture, multilib);
        if (sdk is null || layout is null)
        {
            candidate.Invalidate(
                $"The required {platform}/{architecture}/{multilib ?? "default"} layout did not survive a target-constrained Finder query.");
            return;
        }

        if (!Related(toolSet.RootPath, sdk.RootPath))
        {
            candidate.Invalidate(
                "The target-constrained SDK no longer belongs to the selected bundle ToolSet root.");
            return;
        }

        if (androidApi is int api
            && !layout.ApiLevels.Contains(api)
            && !layout.ApiAliases.ContainsKey(api))
        {
            candidate.Invalidate(
                $"Android API {api} is not backed by this target layout.");
            return;
        }

        string? resolvedTriple = layout.TargetTriple ?? targetTriple;
        if (string.IsNullOrWhiteSpace(resolvedTriple))
        {
            candidate.Invalidate("The bundle target triple is unknown.");
            return;
        }

        ToolQuery query = Query(
            context,
            platform,
            architecture);
        string[] cNames = adapter switch
        {
            BuildAdapterKind.Emscripten => [ToolNames.Emcc],
            _ => [ToolNames.Clang],
        };
        string[] cppNames = adapter switch
        {
            BuildAdapterKind.Emscripten => [ToolNames.Emxx],
            _ => [ToolNames.Clangxx],
        };
        string[] archiveNames = adapter switch
        {
            BuildAdapterKind.Emscripten => [ToolNames.Emar],
            _ => [ToolNames.LlvmAr, ToolNames.Ar],
        };
        string[] ranlibNames = adapter switch
        {
            BuildAdapterKind.Emscripten => [ToolNames.Emranlib],
            _ => [ToolNames.LlvmRanlib, ToolNames.Ranlib],
        };
        Tool? cCompiler = await FindAnyToolAsync(
            context,
            candidate,
            toolSet, cNames, query, cancellationToken).ConfigureAwait(false);
        Tool? cppCompiler = await FindAnyToolAsync(
            context,
            candidate,
            toolSet, cppNames, query, cancellationToken).ConfigureAwait(false);
        Tool? archiver = await FindAnyToolAsync(
            context,
            candidate,
            toolSet, archiveNames, query, cancellationToken).ConfigureAwait(false);
        Tool? ranlib = await FindAnyToolAsync(
            context,
            candidate,
            toolSet, ranlibNames, query, cancellationToken).ConfigureAwait(false);
        if (!RequireTools(
            candidate,
            (cCompiler, cNames[0]),
            (cppCompiler, cppNames[0]),
            (archiver, archiveNames[0])))
        {
            return;
        }

        ExecutionMode executionMode = platform switch
        {
            TargetPlatform.Emscripten => ExecutionMode.Node,
            TargetPlatform.Wasi => ExecutionMode.Wasmtime,
            _ => CanRunNative(context, platform, architecture)
                ? ExecutionMode.Native
                : ExecutionMode.BuildOnly,
        };
        if (!context.Profile.SupportsExecution(executionMode))
        {
            candidate.Invalidate(
                $"Profile '{context.Profile.Name}' does not permit {executionMode} execution.");
            return;
        }

        RuntimeManifest? runtime = executionMode switch
        {
            ExecutionMode.Node => context.FindRuntime(
                RuntimeKind.Node, owner.Requirement.Id),
            ExecutionMode.Wasmtime => context.FindRuntime(RuntimeKind.Wasmtime),
            _ => null,
        };
        if (executionMode is ExecutionMode.Node or ExecutionMode.Wasmtime
            && (runtime is null || !File.Exists(runtime.Path)))
        {
            candidate.Invalidate($"The required {executionMode} runtime is absent.");
            return;
        }

        candidate.Toolchain = new ResolvedToolchain
        {
            Id = id,
            InstallationIds = candidate.InstallationIds,
            AdapterKind = adapter,
            LinkerFlavor = LinkerFlavor.Driver,
            ToolSet = toolSet,
            Sdks = [new ResolvedSdkComponent("bundle", sdk, layout)],
            TargetPlatform = platform,
            TargetArchitecture = architecture,
            TargetTriple = resolvedTriple,
            DriverConfiguration = new DriverConfiguration(resolvedTriple, layout.SysrootPath, layout.Multilib),
            AndroidApi = androidApi,
            CCompiler = cCompiler!,
            CppCompiler = cppCompiler!,
            Archiver = archiver!,
            Ranlib = ranlib,
            Environment = context.EnvironmentFor(owner.Manifest),
            ExecutionMode = executionMode,
            RuntimePath = runtime?.Path,
        };
        candidate.Decisions.Add(
            "Bundle tools and the target-constrained SDK layout were selected from one manifest root before building.");
        candidate.Status = CandidateStatus.Resolved;
    }

    private static (ToolKind ToolKind, SdkKind SdkKind) ExpectedKinds(
        InstallationKind kind) => kind switch
        {
            InstallationKind.AndroidNdk => (ToolKind.AndroidNdk, SdkKind.AndroidNdk),
            InstallationKind.Emscripten => (ToolKind.Emscripten, SdkKind.Emscripten),
            InstallationKind.WasiSdk => (ToolKind.WasiSdk, SdkKind.WasiSdk),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static bool BelongsTo(
        InstallationDiscovery owner,
        ToolSet toolSet) =>
        ToolchainResolution.BelongsTo(owner.Manifest, toolSet);

    private static bool BelongsTo(
        InstallationDiscovery owner,
        Sdk sdk) =>
        ToolchainResolution.BelongsTo(owner.Manifest, sdk);
}
