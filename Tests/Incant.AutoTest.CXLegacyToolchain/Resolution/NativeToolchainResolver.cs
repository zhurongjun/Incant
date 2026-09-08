using Incant.Base;
using Incant.CXLegacy;
using static Incant.AutoTest.CXLegacyToolchain.ToolchainResolution;
using Sdk = Incant.CXLegacy.FindSdk.Sdk;
using SdkFinder = Incant.CXLegacy.FindSdk.Finder;
using SdkKind = Incant.CXLegacy.FindSdk.Kind;
using SdkQuery = Incant.CXLegacy.FindSdk.SdkQuery;
using TargetLayout = Incant.CXLegacy.FindSdk.TargetLayout;
using Tool = Incant.CXLegacy.FindTools.Tool;
using ToolKind = Incant.CXLegacy.FindTools.Kind;
using ToolNames = Incant.CXLegacy.FindTools.ToolNames;
using ToolQuery = Incant.CXLegacy.FindTools.ToolQuery;
using ToolSet = Incant.CXLegacy.FindTools.ToolSet;

namespace Incant.AutoTest.CXLegacyToolchain;

internal static class NativeToolchainResolver
{
    internal static Task ResolveAsync(
        AutoTestContext context,
        CancellationToken cancellationToken) =>
        ResolveNativeCompilersAsync(context, cancellationToken);

    private static async Task ResolveNativeCompilersAsync(
        AutoTestContext context,
        CancellationToken cancellationToken)
    {
        TargetPlatform platform = context.Profile.HostOS == PlatformOS.Linux
            ? TargetPlatform.Linux
            : TargetPlatform.MacOS;
        foreach (InstallationDiscovery owner in context.Installations.Where(
            installation => installation.Requirement.Kind
                is InstallationKind.Gnu or InstallationKind.Llvm))
        {
            foreach (ToolSet toolSet in owner.ToolSets)
            {
                SdkKind compilerKind = toolSet.Kind == ToolKind.Gnu
                    ? SdkKind.Gnu
                    : SdkKind.Llvm;
                Sdk[] compilerSdks = owner.Sdks
                    .Where(sdk => sdk.Kind == compilerKind && CompilerMatches(toolSet, sdk))
                    .ToArray();
                if (compilerSdks.Length == 0)
                {
                    AddInvalid(
                        context,
                        $"{owner.Requirement.Id}-no-compiler-sdk",
                        [owner.Requirement.Id],
                        "No compiler SDK matched the selected compiler path.");
                    continue;
                }

                foreach (Sdk compilerSdk in compilerSdks)
                {
                    TargetLayout[] layouts = compilerSdk.Layouts
                        .Where(layout => IsRequestedLayout(
                            context.Profile, toolSet, platform, layout))
                        .ToArray();
                    foreach (TargetArchitecture architecture in
                        context.Profile.NativeArchitectures)
                    {
                        if (!layouts.Any(layout => layout.Architecture == architecture
                            && layout.Multilib is null or "."))
                        {
                            AddInvalid(
                                context,
                                CreateId(
                                    owner.Requirement.Id,
                                    toolSet.CompilerVersion,
                                    "missing-default",
                                    architecture),
                                [owner.Requirement.Id],
                                $"The required default {platform}/{architecture} layout is absent.");
                        }
                    }

                    foreach (TargetLayout compilerLayout in layouts)
                    {
                        string? triple = compilerLayout.TargetTriple
                            ?? toolSet.DefaultTargetTriple;
                        if (triple is null)
                        {
                            AddInvalid(
                                context,
                                $"{owner.Requirement.Id}-unknown-target",
                                [owner.Requirement.Id],
                                "The compiler target triple is unknown.");
                            continue;
                        }

                        SdkOwner? platformSdk = await FindNativePlatformSdkAsync(
                            context,
                            owner,
                            toolSet,
                            platform,
                            compilerLayout,
                            cancellationToken).ConfigureAwait(false);
                        TargetLayout? platformLayout = platformSdk is null
                            ? null
                            : platform == TargetPlatform.MacOS
                                ? FindLayout(
                                    platformSdk.Sdk,
                                    platform,
                                    compilerLayout.Architecture)
                                : FindLayout(
                                    platformSdk.Sdk,
                                    platform,
                                    compilerLayout.Architecture,
                                    compilerLayout.Multilib);
                        string id = CreateId(
                            toolSet.Kind == ToolKind.Gnu ? "gnu" : "llvm",
                            owner.Requirement.Id,
                            toolSet.CompilerVersion,
                            triple,
                            compilerLayout.Multilib,
                            compilerLayout.Architecture);
                        var installationIds = new List<string>
                        {
                            owner.Requirement.Id,
                        };
                        if (platformSdk is not null)
                        {
                            installationIds.Add(platformSdk.Owner.Requirement.Id);
                        }

                        var candidate = new ToolchainCandidate(id, installationIds,
                            owner.Managed && compilerLayout.Multilib is null or "."
                                && compilerLayout.Architecture == context.Profile.HostArchitecture);
                        context.Candidates.Add(candidate);
                        if (platformSdk is null || platformLayout is null)
                        {
                            candidate.Invalidate(
                                "No platform SDK matched this compiler target and multilib.");
                            continue;
                        }

                        DriverConfiguration driver = DriverConfiguration.Native(platform, compilerLayout, platformLayout);
                        Sdk? selectedCompilerSdk = await FindCompilerSdkAsync(
                            context,
                            owner,
                            toolSet,
                            compilerKind,
                            platform,
                            compilerLayout.Architecture,
                            driver,
                            cancellationToken).ConfigureAwait(false);
                        TargetLayout? selectedCompilerLayout = selectedCompilerSdk is null
                            ? null
                            : FindLayout(
                                selectedCompilerSdk,
                                platform,
                                compilerLayout.Architecture,
                                compilerLayout.Multilib);
                        if (selectedCompilerSdk is null || selectedCompilerLayout is null)
                        {
                            candidate.Invalidate(
                                "The compiler SDK did not confirm this target against the selected platform sysroot.");
                            continue;
                        }

                        ToolQuery query = Query(
                            context, platform, compilerLayout.Architecture);
                        ToolSet? auxiliaryToolSet = platform == TargetPlatform.MacOS
                            ? platformSdk.Owner.ToolSets
                                .Where(candidateToolSet =>
                                    candidateToolSet.Kind == ToolKind.Xcode
                                    && SamePath(
                                        candidateToolSet.EnvironmentPath,
                                        platformSdk.Sdk.EnvironmentPath))
                                .OrderBy(candidateToolSet =>
                                    Path.GetFileName(candidateToolSet.RootPath)
                                        == "XcodeDefault.xctoolchain" ? 0 : 1)
                                .ThenByDescending(candidateToolSet => candidateToolSet.Version)
                                .ThenBy(
                                    candidateToolSet => candidateToolSet.RootPath,
                                    StringComparer.Ordinal)
                                .FirstOrDefault()
                            : null;

                        Tool? cCompiler = await FindToolAsync(
                            context,
                            candidate,
                            toolSet,
                            toolSet.Kind == ToolKind.Gnu
                                ? ToolNames.Gcc
                                : ToolNames.Clang,
                            query,
                            cancellationToken).ConfigureAwait(false);
                        Tool? cppCompiler = await FindToolAsync(
                            context,
                            candidate,
                            toolSet,
                            toolSet.Kind == ToolKind.Gnu
                                ? ToolNames.Gxx
                                : ToolNames.Clangxx,
                            query,
                            cancellationToken).ConfigureAwait(false);
                        Tool? archiver = await FindAnyToolAsync(
                            context,
                            candidate,
                            toolSet,
                            toolSet.Kind == ToolKind.Gnu
                                ? [ToolNames.GccAr, ToolNames.Ar]
                                : [ToolNames.LlvmAr, ToolNames.Ar],
                            query,
                            cancellationToken).ConfigureAwait(false);
                        Tool? ranlib = await FindAnyToolAsync(
                            context,
                            candidate,
                            toolSet,
                            toolSet.Kind == ToolKind.Gnu
                                ? ["gcc-ranlib", ToolNames.Ranlib]
                                : [ToolNames.LlvmRanlib, ToolNames.Ranlib],
                            query,
                            cancellationToken).ConfigureAwait(false);
                        if (!RequireTools(
                            candidate,
                            (cCompiler, "C compiler"),
                            (cppCompiler, "C++ compiler"),
                            (archiver, "archiver")))
                        {
                            continue;
                        }

                        BuildAdapterKind adapter = toolSet.Kind == ToolKind.Gnu
                            ? BuildAdapterKind.Gnu
                            : BuildAdapterKind.Llvm;
                        candidate.Toolchain = new ResolvedToolchain
                        {
                            Id = id,
                            InstallationIds = candidate.InstallationIds,
                            AdapterKind = adapter,
                            LinkerFlavor = LinkerFlavor.Driver,
                            ToolSet = toolSet,
                            AuxiliaryToolSet = auxiliaryToolSet,
                            Sdks =
                            [
                                new ResolvedSdkComponent(
                                    "compiler", selectedCompilerSdk, selectedCompilerLayout),
                                new ResolvedSdkComponent(
                                    "platform", platformSdk.Sdk, platformLayout),
                            ],
                            TargetPlatform = platform,
                            TargetArchitecture = compilerLayout.Architecture,
                            TargetTriple = triple,
                            DriverConfiguration = driver,
                            CCompiler = cCompiler!,
                            CppCompiler = cppCompiler!,
                            Archiver = archiver!,
                            Ranlib = ranlib,
                            Environment = MergeEnvironments(
                                context, owner.Manifest, platformSdk.Owner.Manifest),
                            ExecutionMode = CanRunNative(
                                context,
                                platform,
                                compilerLayout.Architecture,
                                selectedCompilerLayout.Multilib)
                                ? ExecutionMode.Native
                                : ExecutionMode.BuildOnly,
                        };
                        candidate.Decisions.Add(
                            "The compiler and SDK inputs were fixed before building; linking uses the selected driver.");
                        candidate.Status = CandidateStatus.Resolved;
                    }
                }
            }
        }
    }

    private static bool IsRequestedLayout(
        EnvironmentProfile profile,
        ToolSet toolSet,
        TargetPlatform platform,
        TargetLayout layout)
    {
        if (layout.Platform != platform
            || layout.Architecture == TargetArchitecture.Unknown)
        {
            return false;
        }

        if (toolSet.Kind == ToolKind.Gnu && profile.TestAllGnuMultilibs)
        {
            return true;
        }

        return layout.Multilib is null or ".";
    }

    private static async Task<SdkOwner?> FindNativePlatformSdkAsync(
        AutoTestContext context,
        InstallationDiscovery compilerOwner,
        ToolSet toolSet,
        TargetPlatform platform,
        TargetLayout compilerLayout,
        CancellationToken cancellationToken)
    {
        if (platform == TargetPlatform.MacOS)
        {
            return OwnedSdks(context, InstallationKind.Xcode, SdkKind.Apple)
                .Where(item => FindLayout(item.Sdk, platform, compilerLayout.Architecture) is TargetLayout layout
                    && BuildInputs.AppleSdk(layout))
                .OrderByDescending(item => item.Sdk.Version)
                .ThenBy(item => item.Owner.Requirement.Id, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        DriverConfiguration driver = DriverConfiguration.Native(platform, compilerLayout);
        var query = new SdkQuery
        {
            Kind = SdkKind.Linux,
            RootPath = null,
            CompilerPath = toolSet.CompilerPath,
            TargetPlatform = platform,
            TargetArchitecture = compilerLayout.Architecture,
            TargetTriple = driver.TargetTriple,
            SysrootPath = driver.SysrootPath,
            Multilib = driver.Multilib,
            IncludePreview = true,
            Environment = context.EnvironmentFor(compilerOwner.Manifest),
        };
        DiscoveryProbe probe = await DiscoveryStage.RunSdkProbeAsync(
            context,
            $"{compilerOwner.Requirement.Id}/platform/{platform}/{compilerLayout.Architecture}/{compilerLayout.Multilib}",
            "compiler path, target triple, and multilib",
            SdkFinder.CreateDefault(),
            query,
            cancellationToken).ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            return null;
        }

        Sdk? sdk = probe.Sdks
            .Where(candidate => candidate.Kind == query.Kind)
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefault();
        return sdk is null ? null : new SdkOwner(compilerOwner, sdk);
    }
}
