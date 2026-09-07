using Incant.Core.Cpp;
using static Incant.AutoTest.CppToolchain.ToolchainResolution;
using Sdk = Incant.Core.Cpp.FindSdk.Sdk;
using SdkKind = Incant.Core.Cpp.FindSdk.Kind;
using TargetLayout = Incant.Core.Cpp.FindSdk.TargetLayout;
using Tool = Incant.Core.Cpp.FindTools.Tool;
using ToolKind = Incant.Core.Cpp.FindTools.Kind;
using ToolNames = Incant.Core.Cpp.FindTools.ToolNames;
using ToolQuery = Incant.Core.Cpp.FindTools.ToolQuery;
using ToolSet = Incant.Core.Cpp.FindTools.ToolSet;

namespace Incant.AutoTest.CppToolchain;

internal static class XcodeToolchainResolver
{
    internal static async Task ResolveAsync(
        AutoTestContext context,
        CancellationToken cancellationToken)
    {
        foreach (InstallationDiscovery owner in Installations(
            context, InstallationKind.Xcode))
        {
            foreach (ToolSet toolSet in owner.ToolSets
                .Where(toolSet => toolSet.Kind == ToolKind.Xcode))
            {
                foreach (TargetPlatform platform in context.Profile.ApplePlatforms)
                {
                    foreach (TargetArchitecture architecture in
                        context.Profile.AppleArchitectures)
                    {
                        await ResolveTargetAsync(
                            context,
                            owner,
                            toolSet,
                            platform,
                            architecture,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private static async Task ResolveTargetAsync(
        AutoTestContext context,
        InstallationDiscovery owner,
        ToolSet toolSet,
        TargetPlatform platform,
        TargetArchitecture architecture,
        CancellationToken cancellationToken)
    {
        string id = CreateId(
            "xcode",
            owner.Requirement.Id,
            toolSet.ProductVersion,
            platform,
            architecture);
        var candidate = new ToolchainCandidate(id, [owner.Requirement.Id]);
        context.Candidates.Add(candidate);

        Sdk? platformSdk = owner.Sdks
            .Where(sdk => sdk.Kind == SdkKind.Apple
                && SamePath(sdk.EnvironmentPath, toolSet.EnvironmentPath)
                && sdk.Layouts.Any(layout => layout.Platform == platform
                    && layout.Architecture == architecture))
            .OrderByDescending(sdk => sdk.Version)
            .FirstOrDefault();
        TargetLayout? platformLayout = platformSdk is null
            ? null
            : FindLayout(platformSdk, platform, architecture);
        if (platformSdk is null || platformLayout is null)
        {
            candidate.Invalidate(
                "No Apple platform SDK for this target belongs to the selected Xcode environment.");
            return;
        }

        if (platformLayout.SysrootPath is not string sysrootPath)
        {
            candidate.Invalidate("The Apple platform SDK has no confirmed sysroot.");
            return;
        }

        Version? deploymentVersion = platformLayout.MinimumDeploymentVersion
            ?? platformLayout.DefaultDeploymentVersion;
        string triple = TargetTripleIdentity.Apple(
            platform, architecture, deploymentVersion);
        Sdk? compilerSdk = await FindCompilerSdkAsync(
            context,
            owner,
            toolSet,
            SdkKind.AppleClang,
            platform,
            architecture,
            triple,
            multilib: null,
            sysrootPath,
            cancellationToken).ConfigureAwait(false);
        TargetLayout? compilerLayout = compilerSdk is null
            ? null
            : FindLayout(compilerSdk, platform, architecture, triple);
        if (compilerSdk is null || compilerLayout is null)
        {
            candidate.Invalidate(
                "The selected Xcode compiler did not confirm this platform target against its SDK sysroot.");
            return;
        }

        if (!SamePath(compilerSdk.EnvironmentPath, toolSet.EnvironmentPath))
        {
            candidate.Invalidate(
                "The Apple compiler SDK belongs to a different developer environment.");
            return;
        }

        ToolQuery query = Query(context, platform, architecture);
        Tool? cCompiler = await FindToolAsync(
            context,
            candidate,
            toolSet,
            ToolNames.Clang, query, cancellationToken).ConfigureAwait(false);
        Tool? cppCompiler = await FindToolAsync(
            context,
            candidate,
            toolSet,
            ToolNames.Clangxx, query, cancellationToken).ConfigureAwait(false);
        Tool? archiver = await FindToolAsync(
            context,
            candidate,
            toolSet,
            ToolNames.Ar, query, cancellationToken).ConfigureAwait(false);
        Tool? ranlib = await FindToolAsync(
            context,
            candidate,
            toolSet,
            ToolNames.Ranlib, query, cancellationToken).ConfigureAwait(false);
        Tool? linker = await FindToolAsync(
            context,
            candidate,
            toolSet,
            ToolNames.Ld, query, cancellationToken).ConfigureAwait(false);
        if (!RequireTools(
            candidate,
            (cCompiler, "clang"),
            (cppCompiler, "clang++"),
            (archiver, "ar"),
            (ranlib, "ranlib"),
            (linker, "ld")))
        {
            return;
        }

        candidate.Toolchain = new ResolvedToolchain
        {
            Id = id,
            InstallationIds = candidate.InstallationIds,
            AdapterKind = BuildAdapterKind.Apple,
            LinkerFlavor = LinkerFlavor.Driver,
            ToolSet = toolSet,
            Sdks =
            [
                new ResolvedSdkComponent("compiler", compilerSdk, compilerLayout),
                new ResolvedSdkComponent("platform", platformSdk, platformLayout),
            ],
            TargetPlatform = platform,
            TargetArchitecture = architecture,
            TargetTriple = triple,
            Multilib = compilerLayout.Multilib,
            CCompiler = cCompiler!,
            CppCompiler = cppCompiler!,
            Archiver = archiver!,
            Ranlib = ranlib,
            Linker = linker!,
            Environment = context.EnvironmentFor(owner.Manifest),
            ExecutionMode = CanRunNative(context, platform, architecture)
                ? ExecutionMode.Native
                : ExecutionMode.BuildOnly,
        };
        candidate.Decisions.Add(
            "The Xcode compiler, linker, compiler SDK, and platform SDK share one developer environment.");
        candidate.Status = CandidateStatus.Resolved;
    }
}
