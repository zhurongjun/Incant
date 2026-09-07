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
                                    triple,
                                    compilerLayout.Multilib);
                        string id = CreateId(
                            toolSet.Kind == ToolKind.Gnu ? "gnu" : "llvm",
                            owner.Requirement.Id,
                            toolSet.CompilerVersion,
                            TargetTripleIdentity.Canonicalize(triple),
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

                        var candidate = new ToolchainCandidate(id, installationIds);
                        context.Candidates.Add(candidate);
                        if (platformSdk is null || platformLayout is null)
                        {
                            candidate.Invalidate(
                                "No platform SDK matched this compiler target and multilib.");
                            continue;
                        }

                        Sdk? selectedCompilerSdk = await FindCompilerSdkAsync(
                            context,
                            owner,
                            toolSet,
                            compilerKind,
                            platform,
                            compilerLayout.Architecture,
                            triple,
                            compilerLayout.Multilib,
                            platformLayout.SysrootPath,
                            cancellationToken).ConfigureAwait(false);
                        TargetLayout? selectedCompilerLayout = selectedCompilerSdk is null
                            ? null
                            : FindLayout(
                                selectedCompilerSdk,
                                platform,
                                compilerLayout.Architecture,
                                triple,
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
                        if (platform == TargetPlatform.MacOS && auxiliaryToolSet is null)
                        {
                            candidate.Invalidate(
                                "No Xcode ToolSet belongs to the selected Apple platform SDK.");
                            continue;
                        }

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
                        Tool? linker = await FindLinkerAsync(
                            context,
                            candidate,
                            toolSet,
                            auxiliaryToolSet,
                            query,
                            cancellationToken).ConfigureAwait(false);
                        if (!RequireTools(
                            candidate,
                            (cCompiler, "C compiler"),
                            (cppCompiler, "C++ compiler"),
                            (archiver, "archiver"),
                            (ranlib, "ranlib"),
                            (linker, "linker")))
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
                            Multilib = selectedCompilerLayout.Multilib,
                            CCompiler = cCompiler!,
                            CppCompiler = cppCompiler!,
                            Archiver = archiver!,
                            Ranlib = ranlib,
                            Linker = linker!,
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
                            "The compiler, concrete linker, compiler SDK, and platform SDK were fixed before building.");
                        candidate.Status = CandidateStatus.Resolved;
                    }
                }
            }
        }
    }

    private static Task<Tool?> FindLinkerAsync(
        AutoTestContext context,
        ToolchainCandidate candidate,
        ToolSet toolSet,
        ToolSet? auxiliaryToolSet,
        ToolQuery query,
        CancellationToken cancellationToken)
    {
        if (auxiliaryToolSet is not null)
        {
            return FindToolAsync(
                context,
                candidate,
                auxiliaryToolSet,
                ToolNames.Ld,
                query,
                cancellationToken);
        }

        return toolSet.Kind == ToolKind.Gnu
            ? FindToolAsync(
                context,
                candidate,
                toolSet,
                ToolNames.Ld,
                query,
                cancellationToken)
            : FindAnyToolAsync(
                context,
                candidate,
                toolSet,
                [ToolNames.LdLld, ToolNames.Ld],
                query,
                cancellationToken);
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

        return profile.NativeArchitectures.Contains(layout.Architecture)
            && layout.Multilib is null or ".";
    }

    private static async Task<SdkOwner?> FindNativePlatformSdkAsync(
        AutoTestContext context,
        InstallationDiscovery compilerOwner,
        ToolSet toolSet,
        TargetPlatform platform,
        TargetLayout compilerLayout,
        CancellationToken cancellationToken)
    {
        InstallationDiscovery? platformOwner = platform == TargetPlatform.MacOS
            ? Installations(context, InstallationKind.Xcode)
                .Where(owner => owner.Sdks.Any(sdk => sdk.Kind == SdkKind.Apple))
                .OrderByDescending(owner => owner.ToolSets
                    .Select(candidate => candidate.ProductVersion)
                    .Max())
                .FirstOrDefault()
            : compilerOwner;
        if (platformOwner is null)
        {
            return null;
        }

        string? triple = compilerLayout.TargetTriple ?? toolSet.DefaultTargetTriple;
        bool isApplePlatform = platform == TargetPlatform.MacOS;
        IReadOnlyDictionary<string, string?> environment = isApplePlatform
            ? MergeEnvironments(context, compilerOwner.Manifest, platformOwner.Manifest)
            : context.EnvironmentFor(compilerOwner.Manifest);
        var query = new SdkQuery
        {
            Kind = isApplePlatform ? SdkKind.Apple : SdkKind.Linux,
            RootPath = isApplePlatform ? platformOwner.Manifest.RootPath : null,
            CompilerPath = isApplePlatform ? null : toolSet.CompilerPath,
            TargetPlatform = platform,
            TargetArchitecture = compilerLayout.Architecture,
            TargetTriple = isApplePlatform ? null : triple,
            Multilib = isApplePlatform ? null : compilerLayout.Multilib,
            IncludePreview = true,
            Environment = environment,
        };
        DiscoveryProbe probe = await DiscoveryStage.RunSdkProbeAsync(
            context,
            $"{compilerOwner.Requirement.Id}/platform/{platform}/{compilerLayout.Architecture}/{compilerLayout.Multilib}",
            isApplePlatform
                ? "independent Apple platform SDK from the selected Xcode environment"
                : "compiler path, target triple, and multilib",
            SdkFinder.CreateDefault(),
            query,
            cancellationToken).ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            return null;
        }

        Sdk? sdk = probe.Sdks
            .Where(candidate => candidate.Kind == query.Kind
                && BelongsTo(platformOwner.Manifest, candidate))
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefault();
        return sdk is null ? null : new SdkOwner(platformOwner, sdk);
    }
}
