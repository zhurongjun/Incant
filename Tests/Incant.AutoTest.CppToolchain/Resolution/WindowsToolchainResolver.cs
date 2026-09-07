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
    internal static async Task ResolveAsync(AutoTestContext context, CancellationToken cancellationToken)
    {
        TargetArchitecture[] architectures = context.Profile.WindowsMsvcArchitectures
            .Concat(context.Profile.WindowsLlvmArchitectures)
            .Concat(OwnedSdks(context, InstallationKind.VisualStudio, SdkKind.Msvc)
                .SelectMany(item => item.Sdk.Layouts).Select(layout => layout.Architecture))
            .Where(architecture => architecture != TargetArchitecture.Unknown).Distinct().Order().ToArray();
        foreach (TargetArchitecture architecture in architectures)
        {
            SdkOwner[] windows = OwnedSdks(context, InstallationKind.WindowsSdk, SdkKind.Windows)
                .Where(item => FindLayout(item.Sdk, TargetPlatform.Windows, architecture) is TargetLayout layout
                    && BuildInputs.WindowsSdk(layout))
                .OrderByDescending(item => item.Sdk.Version)
                .ThenBy(item => item.Owner.Requirement.Id, StringComparer.Ordinal).ToArray();
            var inputs = new List<MsvcInputs>();
            foreach (InstallationDiscovery owner in Installations(context, InstallationKind.VisualStudio))
            {
                foreach (ToolSet toolSet in owner.ToolSets)
                {
                    MsvcInputs? input = await PrepareMsvcAsync(context, owner, toolSet, architecture,
                        cancellationToken).ConfigureAwait(false);
                    if (input is not null)
                    {
                        inputs.Add(input);
                    }
                }
            }

            MsvcInputs[] complete = inputs.Where(input => input.Compiler is not null
                && input.Archiver is not null && input.Linker is not null)
                .OrderByDescending(input => input.ToolSet.Version)
                .ThenBy(input => input.Owner.Requirement.Id, StringComparer.Ordinal).ToArray();
            if (windows.Length > 0)
            {
                foreach (MsvcInputs input in complete)
                {
                    AddMsvc(context, input, windows[0], architecture);
                }
            }

            if (complete.Length > 0)
            {
                foreach (SdkOwner sdk in windows)
                {
                    AddMsvc(context, complete[0], sdk, architecture);
                }
            }

            if (windows.Length > 0)
            {
                await AddLlvmAsync(context, inputs, windows[0], architecture, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        foreach (InstallationDiscovery owner in context.Installations.Where(owner =>
            owner.Requirement.Kind is InstallationKind.VisualStudio or InstallationKind.WindowsSdk or InstallationKind.Llvm))
        {
            if (!context.Candidates.Any(candidate => candidate.InstallationIds.Contains(owner.Requirement.Id)))
            {
                var candidate = new ToolchainCandidate(owner.Requirement.Id + "-unavailable",
                    [owner.Requirement.Id], owner.Managed);
                candidate.Invalidate("No complete Windows library-chain inputs or compatible component pair were found.");
                context.Candidates.Add(candidate);
            }
        }
    }

    private static async Task<MsvcInputs?> PrepareMsvcAsync(
        AutoTestContext context, InstallationDiscovery owner, ToolSet toolSet,
        TargetArchitecture architecture, CancellationToken cancellationToken)
    {
        string id = CreateId("msvc-inputs", owner.Requirement.Id, architecture);
        var candidate = new ToolchainCandidate(id, [owner.Requirement.Id], owner.Managed);
        Sdk? sdk = await FindCompilerSdkAsync(context, owner, toolSet, SdkKind.Msvc,
            TargetPlatform.Windows, architecture, null, null, null, cancellationToken).ConfigureAwait(false);
        TargetLayout? layout = sdk is null ? null : FindLayout(sdk, TargetPlatform.Windows, architecture);
        if (sdk is null || layout is null || !BuildInputs.MsvcSdk(layout))
        {
            candidate.Invalidate("MSVC headers or runtime libraries used by this target are absent.");
            context.Candidates.Add(candidate);
            return null;
        }

        ToolQuery query = Query(context, TargetPlatform.Windows, architecture);
        Tool? compiler = await FindToolAsync(context, candidate, toolSet, ToolNames.Cl, query, cancellationToken)
            .ConfigureAwait(false);
        Tool? archiver = await FindToolAsync(context, candidate, toolSet, ToolNames.Lib, query, cancellationToken)
            .ConfigureAwait(false);
        Tool? linker = await FindToolAsync(context, candidate, toolSet, ToolNames.Link, query, cancellationToken)
            .ConfigureAwait(false);
        if (compiler is null || archiver is null || linker is null)
        {
            candidate.Invalidate("MSVC cannot run the full library chain; its available SDK can still serve clang-cl.");
            context.Candidates.Add(candidate);
        }

        return new MsvcInputs(owner, toolSet, sdk, layout, compiler, archiver, linker);
    }

    private static void AddMsvc(
        AutoTestContext context, MsvcInputs input, SdkOwner windows, TargetArchitecture architecture)
    {
        string id = CreateId("msvc", input.Owner.Requirement.Id, windows.Owner.Requirement.Id, architecture);
        if (context.Candidates.Any(candidate => candidate.Id == id))
        {
            return;
        }

        var candidate = new ToolchainCandidate(id,
            [input.Owner.Requirement.Id, windows.Owner.Requirement.Id], input.Owner.Managed || windows.Owner.Managed);
        candidate.Toolchain = new ResolvedToolchain
        {
            Id = id,
            InstallationIds = candidate.InstallationIds,
            AdapterKind = BuildAdapterKind.Msvc,
            LinkerFlavor = LinkerFlavor.Msvc,
            ToolSet = input.ToolSet,
            Sdks =
            [
                new ResolvedSdkComponent("compiler", input.Sdk, input.Layout),
                new ResolvedSdkComponent("platform", windows.Sdk,
                    FindLayout(windows.Sdk, TargetPlatform.Windows, architecture)!),
            ],
            TargetPlatform = TargetPlatform.Windows,
            TargetArchitecture = architecture,
            TargetTriple = WindowsTriple(architecture),
            CCompiler = input.Compiler!,
            CppCompiler = input.Compiler!,
            Archiver = input.Archiver!,
            Linker = input.Linker!,
            Environment = MergeEnvironments(context, input.Owner.Manifest, windows.Owner.Manifest),
            ExecutionMode = CanRunNative(context, TargetPlatform.Windows, architecture)
                ? ExecutionMode.Native : ExecutionMode.BuildOnly,
        };
        candidate.Status = CandidateStatus.Resolved;
        candidate.Decisions.Add("Selected the highest available complete companion before building.");
        context.Candidates.Add(candidate);
    }

    private static async Task AddLlvmAsync(
        AutoTestContext context, IReadOnlyList<MsvcInputs> msvcInputs, SdkOwner windows,
        TargetArchitecture architecture, CancellationToken cancellationToken)
    {
        foreach (InstallationDiscovery owner in Installations(context, InstallationKind.Llvm))
        {
            foreach (ToolSet toolSet in owner.ToolSets)
            {
                foreach (LinkerFlavor flavor in new[] { LinkerFlavor.Msvc, LinkerFlavor.Lld })
                {
                    MsvcInputs? msvc = msvcInputs.Where(input => flavor != LinkerFlavor.Msvc || input.Linker is not null)
                        .OrderByDescending(input => input.ToolSet.Version)
                        .ThenBy(input => input.Owner.Requirement.Id, StringComparer.Ordinal).FirstOrDefault();
                    string id = CreateId("clang-cl", owner.Requirement.Id,
                        msvc?.Owner.Requirement.Id, windows.Owner.Requirement.Id, architecture, flavor);
                    var candidate = new ToolchainCandidate(id,
                        new[] { owner.Requirement.Id, windows.Owner.Requirement.Id }
                            .Concat(msvc is null ? [] : new[] { msvc.Owner.Requirement.Id }), owner.Managed);
                    context.Candidates.Add(candidate);
                    if (msvc is null)
                    {
                        candidate.Invalidate("No usable MSVC runtime environment matches this linker scenario.");
                        continue;
                    }

                    string triple = WindowsTriple(architecture);
                    Sdk? compilerSdk = await FindCompilerSdkAsync(context, owner, toolSet, SdkKind.Llvm,
                        TargetPlatform.Windows, architecture, triple, null, null, cancellationToken).ConfigureAwait(false);
                    TargetLayout? compilerLayout = compilerSdk is null ? null
                        : FindLayout(compilerSdk, TargetPlatform.Windows, architecture);
                    if (compilerSdk is null || compilerLayout is null)
                    {
                        candidate.Invalidate("No compiler SDK confirms this Windows target.");
                        continue;
                    }

                    ToolQuery query = Query(context, TargetPlatform.Windows, architecture);
                    Tool? compiler = await FindToolAsync(context, candidate, toolSet, ToolNames.ClangCl,
                        query, cancellationToken).ConfigureAwait(false);
                    Tool? archiver = await FindAnyToolAsync(context, candidate, toolSet,
                        [ToolNames.LlvmLib, ToolNames.LlvmAr], query, cancellationToken).ConfigureAwait(false);
                    Tool? linker = flavor == LinkerFlavor.Msvc ? msvc.Linker
                        : await FindToolAsync(context, candidate, toolSet, ToolNames.LldLink, query, cancellationToken)
                            .ConfigureAwait(false);
                    if (!RequireTools(candidate, (compiler, "clang-cl"), (archiver, "archiver"), (linker, "explicit linker")))
                    {
                        continue;
                    }

                    candidate.Toolchain = new ResolvedToolchain
                    {
                        Id = id,
                        InstallationIds = candidate.InstallationIds,
                        AdapterKind = BuildAdapterKind.ClangCl,
                        LinkerFlavor = flavor,
                        ToolSet = toolSet,
                        AuxiliaryToolSet = msvc.ToolSet,
                        Sdks =
                        [
                            new ResolvedSdkComponent("compiler", compilerSdk, compilerLayout),
                            new ResolvedSdkComponent("msvc", msvc.Sdk, msvc.Layout),
                            new ResolvedSdkComponent("platform", windows.Sdk,
                                FindLayout(windows.Sdk, TargetPlatform.Windows, architecture)!),
                        ],
                        TargetPlatform = TargetPlatform.Windows,
                        TargetArchitecture = architecture,
                        TargetTriple = triple,
                        CCompiler = compiler!,
                        CppCompiler = compiler!,
                        Archiver = archiver!,
                        Linker = linker!,
                        Environment = MergeEnvironments(context, owner.Manifest, msvc.Owner.Manifest, windows.Owner.Manifest),
                        ExecutionMode = CanRunNative(context, TargetPlatform.Windows, architecture)
                            ? ExecutionMode.Native : ExecutionMode.BuildOnly,
                    };
                    candidate.Decisions.Add($"The {flavor} linker is executed explicitly; inputs were fixed before compilation.");
                    candidate.Status = CandidateStatus.Resolved;
                }
            }
        }
    }

    private sealed record MsvcInputs(
        InstallationDiscovery Owner, ToolSet ToolSet, Sdk Sdk, TargetLayout Layout,
        Tool? Compiler, Tool? Archiver, Tool? Linker);
}
