using Incant.Base;
using Incant.Core.Cpp;
using static Incant.AutoTest.CppToolchain.ToolchainResolution;
using ResourcePurpose = Incant.Core.Cpp.FindSdk.ResourcePurpose;
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

internal static class WindowsToolchainResolver
{
    internal static async Task ResolveAsync(
        AutoTestContext context,
        CancellationToken cancellationToken)
    {
        await ResolveWindowsMsvcAsync(context, cancellationToken).ConfigureAwait(false);
        await ResolveWindowsLlvmAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ResolveWindowsMsvcAsync(
        AutoTestContext context,
        CancellationToken cancellationToken)
    {
        InstallationDiscovery[] visualStudios = Installations(
            context, InstallationKind.VisualStudio).ToArray();
        SdkOwner[] windowsSdks = OwnedSdks(context, InstallationKind.WindowsSdk, SdkKind.Windows)
            .OrderByDescending(item => item.Sdk.Version)
            .ToArray();
        if (windowsSdks.Length == 0)
        {
            return;
        }

        SdkOwner highestWindowsSdk = windowsSdks[0];
        var toolSets = new List<ToolSetOwner>();
        foreach (InstallationDiscovery owner in visualStudios)
        {
            toolSets.AddRange(owner.ToolSets
                .Where(toolSet => toolSet.Kind == ToolKind.VisualStudio)
                .Select(toolSet => new ToolSetOwner(owner, toolSet)));
        }

        ToolSetOwner? highestToolSet = toolSets
            .OrderByDescending(item => item.ToolSet.Version)
            .FirstOrDefault();
        if (highestToolSet is null)
        {
            return;
        }

        var compilerSdks =
            new Dictionary<(ToolSet ToolSet, TargetArchitecture Architecture), Sdk?>();
        foreach (ToolSetOwner toolSet in toolSets)
        {
            Sdk? msvcSdk = FindMsvcSdk(toolSet.ToolSet, toolSet.Owner.Sdks);
            TargetArchitecture[] architectures =
                context.Profile.UseExistingMsvcTargetsForNonDefaultToolSets
                && !ReferenceEquals(toolSet.ToolSet, highestToolSet.ToolSet)
                && msvcSdk is not null
                    ? msvcSdk.Layouts
                        .Where(layout => layout.Platform == TargetPlatform.Windows
                            && layout.Architecture != TargetArchitecture.Unknown
                            && IsCompleteLayout(
                                msvcSdk,
                                layout,
                                ResourcePurpose.CppInclude,
                                ResourcePurpose.Library)
                            && FindLayout(
                                highestWindowsSdk.Sdk,
                                TargetPlatform.Windows,
                                layout.Architecture) is TargetLayout windowsLayout
                            && IsCompleteLayout(
                                highestWindowsSdk.Sdk,
                                windowsLayout,
                                ResourcePurpose.CInclude,
                                ResourcePurpose.Library))
                        .Select(layout => layout.Architecture)
                        .Distinct()
                        .Order()
                        .ToArray()
                    : context.Profile.WindowsMsvcArchitectures.ToArray();
            if (architectures.Length == 0)
            {
                AddInvalid(
                    context,
                    CreateId("msvc", toolSet.ToolSet.Version, "no-complete-target"),
                    [toolSet.Owner.Requirement.Id],
                    "The compatible MSVC ToolSet has no complete installed target.");
                continue;
            }

            foreach (TargetArchitecture architecture in architectures)
            {
                await AddWindowsMsvcCandidateAsync(
                    context,
                    toolSet,
                    highestWindowsSdk,
                    architecture,
                    compilerSdks,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        foreach (SdkOwner windowsSdk in windowsSdks)
        {
            foreach (TargetArchitecture architecture in context.Profile.WindowsMsvcArchitectures)
            {
                await AddWindowsMsvcCandidateAsync(
                    context,
                    highestToolSet,
                    windowsSdk,
                    architecture,
                    compilerSdks,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task AddWindowsMsvcCandidateAsync(
        AutoTestContext context,
        ToolSetOwner toolSetOwner,
        SdkOwner windowsSdkOwner,
        TargetArchitecture architecture,
        Dictionary<(ToolSet ToolSet, TargetArchitecture Architecture), Sdk?> compilerSdks,
        CancellationToken cancellationToken)
    {
        ToolSet toolSet = toolSetOwner.ToolSet;
        string id = CreateId(
            "msvc",
            toolSet.Version,
            windowsSdkOwner.Sdk.Version,
            architecture);
        if (context.Candidates.Any(candidate => candidate.Id == id))
        {
            return;
        }

        var candidate = new ToolchainCandidate(
            id,
            [toolSetOwner.Owner.Requirement.Id, windowsSdkOwner.Owner.Requirement.Id]);
        context.Candidates.Add(candidate);
        var compilerSdkKey = (toolSet, architecture);
        if (!compilerSdks.TryGetValue(compilerSdkKey, out Sdk? msvcSdk))
        {
            msvcSdk = await FindCompilerSdkAsync(
                context,
                toolSetOwner.Owner,
                toolSet,
                SdkKind.Msvc,
                TargetPlatform.Windows,
                architecture,
                triple: null,
                multilib: null,
                sysrootPath: null,
                cancellationToken).ConfigureAwait(false);
            compilerSdks.Add(compilerSdkKey, msvcSdk);
        }

        if (msvcSdk is null)
        {
            candidate.Invalidate("No MSVC development SDK belongs to this ToolSet.");
            return;
        }

        TargetLayout? msvcLayout = FindLayout(
            msvcSdk, TargetPlatform.Windows, architecture);
        TargetLayout? windowsLayout = FindLayout(
            windowsSdkOwner.Sdk, TargetPlatform.Windows, architecture);
        if (msvcLayout is null || windowsLayout is null)
        {
            candidate.Invalidate(
                $"The {architecture} target is not installed completely in this ToolSet/SDK pair.");
            return;
        }

        ToolQuery query = Query(context, TargetPlatform.Windows, architecture);
        Tool? compiler = await FindToolAsync(
            candidate,
            toolSet,
            ToolNames.Cl, query, cancellationToken).ConfigureAwait(false);
        Tool? archiver = await FindToolAsync(
            candidate,
            toolSet,
            ToolNames.Lib, query, cancellationToken).ConfigureAwait(false);
        Tool? linker = await FindToolAsync(
            candidate,
            toolSet,
            ToolNames.Link, query, cancellationToken).ConfigureAwait(false);
        if (!RequireTools(
            candidate,
            (compiler, "cl"),
            (archiver, "lib"),
            (linker, "link")))
        {
            return;
        }

        candidate.Toolchain = new ResolvedToolchain
        {
            Id = id,
            InstallationIds = candidate.InstallationIds,
            AdapterKind = BuildAdapterKind.Msvc,
            LinkerFlavor = LinkerFlavor.Msvc,
            ToolSet = toolSet,
            Sdks =
            [
                new ResolvedSdkComponent("compiler", msvcSdk, msvcLayout),
                new ResolvedSdkComponent("platform", windowsSdkOwner.Sdk, windowsLayout),
            ],
            TargetPlatform = TargetPlatform.Windows,
            TargetArchitecture = architecture,
            TargetTriple = WindowsTriple(architecture),
            CCompiler = compiler!,
            CppCompiler = compiler!,
            Archiver = archiver!,
            Linker = linker!,
            Environment = MergeEnvironments(
                context, toolSetOwner.Owner.Manifest, windowsSdkOwner.Owner.Manifest),
            ExecutionMode = CanRunNative(context, TargetPlatform.Windows, architecture)
                ? ExecutionMode.Native
                : ExecutionMode.BuildOnly,
        };
        candidate.Decisions.Add(
            "Paired one concrete MSVC ToolSet with the selected Windows SDK before building.");
        candidate.Status = CandidateStatus.Resolved;
    }

    private static bool IsCompleteLayout(
        Sdk sdk,
        TargetLayout layout,
        params ResourcePurpose[] requiredResources) =>
        !sdk.Diagnostics.Concat(layout.Diagnostics).Any(
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error
                || diagnostic.Code == "missing-resource")
        && requiredResources.All(required => layout.Resources.Any(
            resource => resource.Purpose == required));

    private static async Task ResolveWindowsLlvmAsync(
        AutoTestContext context,
        CancellationToken cancellationToken)
    {
        ToolSetOwner? msvcToolSet = Installations(context, InstallationKind.VisualStudio)
            .SelectMany(owner => owner.ToolSets
                .Where(toolSet => toolSet.Kind == ToolKind.VisualStudio)
                .Select(toolSet => new ToolSetOwner(owner, toolSet)))
            .OrderByDescending(item => item.ToolSet.Version)
            .FirstOrDefault();
        SdkOwner? windowsSdk = OwnedSdks(
                context, InstallationKind.WindowsSdk, SdkKind.Windows)
            .OrderByDescending(item => item.Sdk.Version)
            .FirstOrDefault();
        if (msvcToolSet is null || windowsSdk is null)
        {
            return;
        }

        if (FindMsvcSdk(msvcToolSet.ToolSet, msvcToolSet.Owner.Sdks) is null)
        {
            return;
        }

        var msvcSdks = new Dictionary<TargetArchitecture, Sdk?>();
        foreach (InstallationDiscovery llvmOwner in Installations(
            context, InstallationKind.Llvm))
        {
            foreach (ToolSet llvmToolSet in llvmOwner.ToolSets
                .Where(toolSet => toolSet.Kind == ToolKind.Llvm))
            {
                foreach (TargetArchitecture architecture in
                    context.Profile.WindowsLlvmArchitectures)
                {
                    string triple = WindowsTriple(architecture);
                    if (!msvcSdks.TryGetValue(architecture, out Sdk? msvcSdk))
                    {
                        msvcSdk = await FindCompilerSdkAsync(
                            context,
                            msvcToolSet.Owner,
                            msvcToolSet.ToolSet,
                            SdkKind.Msvc,
                            TargetPlatform.Windows,
                            architecture,
                            triple: null,
                            multilib: null,
                            sysrootPath: null,
                            cancellationToken).ConfigureAwait(false);
                        msvcSdks.Add(architecture, msvcSdk);
                    }

                    Sdk? llvmSdk = await FindCompilerSdkAsync(
                        context,
                        llvmOwner,
                        llvmToolSet,
                        SdkKind.Llvm,
                        TargetPlatform.Windows,
                        architecture,
                        triple,
                        multilib: null,
                        sysrootPath: null,
                        cancellationToken).ConfigureAwait(false);
                    TargetLayout? llvmLayout = llvmSdk is null
                        ? null
                        : FindLayout(
                            llvmSdk, TargetPlatform.Windows, architecture, triple);
                    TargetLayout? msvcLayout = msvcSdk is null
                        ? null
                        : FindLayout(
                            msvcSdk, TargetPlatform.Windows, architecture);
                    TargetLayout? windowsLayout = FindLayout(
                        windowsSdk.Sdk, TargetPlatform.Windows, architecture);

                    foreach (LinkerFlavor flavor in new[]
                    {
                        LinkerFlavor.Msvc,
                        LinkerFlavor.Lld,
                    })
                    {
                        string id = CreateId(
                            "clang-cl-" + flavor.ToString().ToLowerInvariant(),
                            llvmToolSet.CompilerVersion,
                            windowsSdk.Sdk.Version,
                            architecture);
                        var candidate = new ToolchainCandidate(
                            id,
                            [
                                llvmOwner.Requirement.Id,
                                msvcToolSet.Owner.Requirement.Id,
                                windowsSdk.Owner.Requirement.Id,
                            ]);
                        context.Candidates.Add(candidate);
                        if (llvmSdk is null
                            || llvmLayout is null
                            || msvcSdk is null
                            || msvcLayout is null
                            || windowsLayout is null)
                        {
                            candidate.Invalidate(
                                "LLVM, MSVC, and Windows SDK layouts could not be resolved for this target.");
                            continue;
                        }

                        ToolQuery query = Query(
                            context, TargetPlatform.Windows, architecture);
                        Tool? compiler = await FindToolAsync(
                            candidate,
                            llvmToolSet,
                            ToolNames.ClangCl, query, cancellationToken).ConfigureAwait(false);
                        Tool? archiver = await FindAnyToolAsync(
                            candidate,
                            llvmToolSet,
                            [ToolNames.LlvmLib, ToolNames.LlvmAr],
                            query,
                            cancellationToken).ConfigureAwait(false);
                        Tool? linker = flavor == LinkerFlavor.Msvc
                            ? await FindToolAsync(
                                candidate,
                                msvcToolSet.ToolSet,
                                ToolNames.Link, query, cancellationToken).ConfigureAwait(false)
                            : await FindToolAsync(
                                candidate,
                                llvmToolSet,
                                ToolNames.LldLink, query, cancellationToken).ConfigureAwait(false);
                        if (!RequireTools(
                            candidate,
                            (compiler, "clang-cl"),
                            (archiver, "llvm-lib/llvm-ar"),
                            (linker, flavor == LinkerFlavor.Msvc
                                ? "link"
                                : "lld-link")))
                        {
                            continue;
                        }

                        candidate.Toolchain = new ResolvedToolchain
                        {
                            Id = id,
                            InstallationIds = candidate.InstallationIds,
                            AdapterKind = BuildAdapterKind.ClangCl,
                            LinkerFlavor = flavor,
                            ToolSet = llvmToolSet,
                            AuxiliaryToolSet = msvcToolSet.ToolSet,
                            Sdks =
                            [
                                new ResolvedSdkComponent("compiler", llvmSdk, llvmLayout),
                                new ResolvedSdkComponent("msvc", msvcSdk, msvcLayout),
                                new ResolvedSdkComponent("platform", windowsSdk.Sdk, windowsLayout),
                            ],
                            TargetPlatform = TargetPlatform.Windows,
                            TargetArchitecture = architecture,
                            TargetTriple = triple,
                            CCompiler = compiler!,
                            CppCompiler = compiler!,
                            Archiver = archiver!,
                            Linker = linker!,
                            Environment = MergeEnvironments(
                                context,
                                llvmOwner.Manifest,
                                msvcToolSet.Owner.Manifest,
                                windowsSdk.Owner.Manifest),
                            ExecutionMode = CanRunNative(
                                context, TargetPlatform.Windows, architecture)
                                ? ExecutionMode.Native
                                : ExecutionMode.BuildOnly,
                        };
                        candidate.Decisions.Add(
                            $"clang-cl was fixed to the {flavor} linker before compilation.");
                        candidate.Status = CandidateStatus.Resolved;
                    }
                }
            }
        }
    }
}
