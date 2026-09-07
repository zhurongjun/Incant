using Incant.Core.Cpp;
using Incant.Core.Cpp.FindSdk;
using Incant.Core.Cpp.FindTools;
using SdkKind = Incant.Core.Cpp.FindSdk.Kind;

namespace Incant.AutoTest.CppToolchain;

internal static class ValidationStage
{
    internal static Task<bool> ExecuteAsync(
        AutoTestContext context,
        CancellationToken cancellationToken)
    {
        ToolchainCandidate[] candidates = context.Candidates
            .Where(candidate => candidate.Status == CandidateStatus.Resolved)
            .ToArray();
        foreach (ToolchainCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCandidate(context, candidate);
            if (!context.ContinueAfter(candidate))
            {
                break;
            }
        }

        ValidateEmscriptenVariants(context);
        return Task.FromResult(context.RequiredCandidatesSatisfy(
            candidates,
            candidate => candidate.Status == CandidateStatus.Resolved));
    }

    private static void ValidateCandidate(
        AutoTestContext context,
        ToolchainCandidate candidate)
    {
        ResolvedToolchain toolchain = candidate.Toolchain!;
        if (!context.Profile.SupportsExecution(toolchain.ExecutionMode))
        {
            candidate.Invalidate(
                $"Profile '{context.Profile.Name}' does not permit {toolchain.ExecutionMode} execution.");
        }
        ValidateToolSet(candidate, toolchain.ToolSet, "ToolSet");
        if (toolchain.AuxiliaryToolSet is ToolSet auxiliaryToolSet)
        {
            ValidateToolSet(candidate, auxiliaryToolSet, "auxiliary ToolSet");
        }

        ValidateTool(context, candidate, toolchain.CCompiler, "C compiler");
        ValidateTool(context, candidate, toolchain.CppCompiler, "C++ compiler");
        ValidateTool(context, candidate, toolchain.Archiver, "archiver");
        ValidateTool(context, candidate, toolchain.Linker, "linker");
        if (toolchain.Ranlib is not null)
        {
            ValidateTool(context, candidate, toolchain.Ranlib, "ranlib");
        }

        if (toolchain.ExecutionMode is ExecutionMode.Node or ExecutionMode.Wasmtime)
        {
            ValidateAbsoluteExistingPath(
                candidate,
                toolchain.RuntimePath ?? string.Empty,
                "execution runtime",
                expectDirectory: false);
        }

        foreach (ResolvedSdkComponent component in toolchain.Sdks)
        {
            ValidateSdkComponent(context.Profile, candidate, toolchain, component);
        }
        ValidateMsvcComponents(candidate, toolchain);

        Resource[] resources = toolchain.Resources.ToArray();
        if (!resources.Any(resource => resource.Purpose == ResourcePurpose.CInclude))
        {
            candidate.Invalidate("No C include directory exists in the selected SDK set.");
        }

        if (!resources.Any(resource => resource.Purpose == ResourcePurpose.CppInclude))
        {
            candidate.Invalidate("No C++ include directory exists in the selected SDK set.");
        }

        if (!resources.Any(resource => resource.Purpose == ResourcePurpose.Library))
        {
            candidate.Invalidate(
                "No concrete library file exists in the selected SDK set.");
        }

        foreach (string installationId in toolchain.InstallationIds)
        {
            ValidateRequirementIdentity(context, candidate, installationId);
        }

        if (!TargetTripleIdentity.Matches(
            toolchain.TargetTriple,
            toolchain.TargetPlatform,
            toolchain.TargetArchitecture))
        {
            candidate.Invalidate(
                $"Target triple '{toolchain.TargetTriple}' does not describe {toolchain.TargetPlatform}/{toolchain.TargetArchitecture}.");
        }

        if (candidate.Failures.Count == 0)
        {
            candidate.Decisions.Add(
                "Identity, tools, target layouts, resource order, ownership, and diagnostics passed validation.");
        }
    }

    private static void ValidateToolSet(
        ToolchainCandidate candidate,
        ToolSet toolSet,
        string role)
    {
        ValidateAbsoluteExistingPath(
            candidate,
            toolSet.RootPath,
            $"{role} root",
            expectDirectory: true);
        ValidateAbsoluteExistingPath(
            candidate,
            toolSet.EnvironmentPath,
            $"{role} environment",
            expectDirectory: true);
        if (toolSet.CompilerPath is string compilerPath)
        {
            ValidateAbsoluteExistingPath(
                candidate,
                compilerPath,
                $"{role} identity compiler",
                expectDirectory: false);
        }

        if (toolSet.Sources.Count == 0)
        {
            candidate.Invalidate($"The {role} has no discovery provenance.");
        }

        foreach (Diagnostic diagnostic in toolSet.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            candidate.Invalidate(
                $"{role} diagnostic {diagnostic.Code}: {diagnostic.Message}");
        }
    }

    private static void ValidateRequirementIdentity(
        AutoTestContext context,
        ToolchainCandidate candidate,
        string installationId)
    {
        InstallationDiscovery installation = context.Installations
            .Single(item => item.Requirement.Id == installationId);
        VersionRule? toolRule = installation.Requirement.ToolVersion;
        VersionRule? sdkRule = installation.Requirement.SdkVersion;
        ResolvedToolchain toolchain = candidate.Toolchain!;
        ToolSet[] matchingToolSets = new[]
        {
            toolchain.ToolSet,
            toolchain.AuxiliaryToolSet,
        }
            .OfType<ToolSet>()
            .Where(toolSet => Related(installation.Manifest.RootPath, toolSet)
                && (toolRule is null || toolRule.Matches(toolSet)))
            .ToArray();
        Sdk[] matchingSdks = toolchain.Sdks
            .Select(component => component.Sdk)
            .Where(sdk => Related(installation.Manifest.RootPath, sdk)
                && (sdkRule is null || SdkVersionMatches(sdkRule, sdk)))
            .ToArray();
        if (toolRule is not null && matchingToolSets.Length == 0)
        {
            candidate.Invalidate(
                $"No selected ToolSet proves the declared identity of '{installationId}'.");
        }

        if (sdkRule is not null && matchingSdks.Length == 0)
        {
            candidate.Invalidate(
                $"No selected SDK proves the declared identity of '{installationId}'.");
        }

        foreach ((IReadOnlyList<Source> sources, string subject) in matchingToolSets
            .Select(toolSet => (toolSet.Sources, "ToolSet"))
            .Concat(matchingSdks.Select(sdk => (sdk.Sources, "SDK"))))
        {
            if (!sources.Contains(Source.Explicit))
            {
                candidate.Invalidate(
                    $"The selected {subject} for '{installationId}' has no explicit-root provenance.");
            }
        }

        Channel[] channels = matchingToolSets
            .Select(toolSet => toolSet.Channel)
            .Concat(matchingSdks.Select(sdk => sdk.Channel))
            .Distinct()
            .ToArray();
        if (channels.Contains(Channel.Unknown))
        {
            candidate.Invalidate(
                $"The release channel of '{installationId}' could not be established.");
        }

        if (channels.Length > 1)
        {
            candidate.Invalidate(
                $"Selected components for '{installationId}' disagree on release channel: "
                + string.Join(", ", channels) + ".");
        }
    }

    private static bool SdkVersionMatches(VersionRule rule, Sdk sdk)
    {
        Version? actual = rule.Source == VersionSource.ProductVersion
            ? sdk.ProductVersion
            : sdk.Version;
        return rule.Constraint.Matches(actual);
    }

    private static void ValidateSdkComponent(
        EnvironmentProfile profile,
        ToolchainCandidate candidate,
        ResolvedToolchain toolchain,
        ResolvedSdkComponent component)
    {
        Sdk sdk = component.Sdk;
        TargetLayout layout = component.Layout;
        ValidateAbsoluteExistingPath(
            candidate,
            sdk.RootPath,
            $"{component.Role} SDK root",
            expectDirectory: true);
        ValidateAbsoluteExistingPath(
            candidate,
            sdk.EnvironmentPath,
            $"{component.Role} SDK environment",
            expectDirectory: true);
        if (sdk.CompilerPath is string compilerPath)
        {
            ValidateAbsoluteExistingPath(
                candidate,
                compilerPath,
                $"{component.Role} SDK compiler",
                expectDirectory: false);
        }

        if (layout.SysrootPath is string sysrootPath)
        {
            ValidateAbsoluteExistingPath(
                candidate,
                sysrootPath,
                $"{component.Role} SDK sysroot",
                expectDirectory: true);
        }

        if (component.Role == "compiler"
            && (sdk.CompilerPath is null
                || toolchain.ToolSet.CompilerPath is null
                || !PathIdentity.Comparer.Equals(
                    PathIdentity.Normalize(sdk.CompilerPath),
                    PathIdentity.Normalize(toolchain.ToolSet.CompilerPath))))
        {
            candidate.Invalidate(
                "The compiler SDK is not owned by the selected ToolSet compiler.");
        }

        if (sdk.Sources.Count == 0)
        {
            candidate.Invalidate($"{component.Role} SDK has no discovery provenance.");
        }

        if (layout.Platform != toolchain.TargetPlatform
            || layout.Architecture != toolchain.TargetArchitecture)
        {
            candidate.Invalidate(
                $"{component.Role} SDK layout does not match the resolved target.");
        }

        if (layout.TargetTriple is string layoutTriple
            && !TargetTripleIdentity.AreEquivalent(layoutTriple, toolchain.TargetTriple))
        {
            candidate.Invalidate(
                $"{component.Role} SDK triple '{layoutTriple}' differs from '{toolchain.TargetTriple}'.");
        }

        if (component.Role is "compiler" or "bundle"
            && layout.Multilib != toolchain.Multilib)
        {
            candidate.Invalidate(
                $"{component.Role} SDK multilib '{layout.Multilib}' differs from '{toolchain.Multilib}'.");
        }

        if (toolchain.AndroidApi is int api
            && !layout.ApiLevels.Contains(api)
            && !layout.ApiAliases.ContainsKey(api))
        {
            candidate.Invalidate(
                $"{component.Role} SDK does not contain Android API {api}.");
        }

        foreach (Diagnostic diagnostic in sdk.Diagnostics.Concat(layout.Diagnostics))
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                candidate.Invalidate(
                    $"{component.Role} SDK diagnostic {diagnostic.Code}: {diagnostic.Message}");
            }
        }

        ValidateRequiredResourceGroups(profile, candidate, component);

        string[] duplicateResources = layout.Resources
            .GroupBy(
                resource => (
                    resource.Purpose,
                    Path: PathIdentity.Normalize(resource.Path),
                    resource.ApiLevel),
                ResourceKeyComparer.Instance)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.Purpose}:{group.Key.Path}:{group.Key.ApiLevel}")
            .ToArray();
        if (duplicateResources.Length > 0)
        {
            candidate.Invalidate(
                $"{component.Role} SDK layout contains duplicate resources: "
                + string.Join(", ", duplicateResources));
        }

        ValidateResourceOrder(candidate, component);
        foreach (Resource resource in layout.Resources)
        {
            ValidateResource(candidate, sdk, resource);
        }
    }

    private static void ValidateMsvcComponents(
        ToolchainCandidate candidate,
        ResolvedToolchain toolchain)
    {
        if (toolchain.AdapterKind is not BuildAdapterKind.Msvc
            and not BuildAdapterKind.ClangCl)
        {
            return;
        }

        ToolSet? msvcToolSet = toolchain.AdapterKind == BuildAdapterKind.Msvc
            ? toolchain.ToolSet
            : toolchain.AuxiliaryToolSet;
        if (msvcToolSet is null)
        {
            candidate.Invalidate(
                "The Windows toolchain has no concrete MSVC ToolSet.");
            return;
        }

        ResolvedSdkComponent[] components = toolchain.Sdks
            .Where(component => component.Sdk.Kind == SdkKind.Msvc)
            .ToArray();
        if (components.Length != 1)
        {
            candidate.Invalidate(
                $"The Windows toolchain must contain exactly one MSVC SDK component; found {components.Length}.");
            return;
        }

        Sdk sdk = components[0].Sdk;
        if (!ToolchainResolution.MsvcIdentityMatches(msvcToolSet, sdk))
        {
            candidate.Invalidate(
                $"MSVC SDK '{sdk.RootPath}' version '{sdk.Version}' does not belong to "
                + $"ToolSet '{msvcToolSet.RootPath}' version '{msvcToolSet.Version}'.");
        }

        if (!ToolchainResolution.CompilerMatches(msvcToolSet, sdk))
        {
            candidate.Invalidate(
                "The MSVC SDK compiler identity does not match its concrete ToolSet.");
        }
    }

    private static void ValidateRequiredResourceGroups(
        EnvironmentProfile profile,
        ToolchainCandidate candidate,
        ResolvedSdkComponent component)
    {
        if (!profile.RequiredSdkResources.TryGetValue(
            component.Sdk.Kind, out IReadOnlyList<ResourcePurpose>? required))
        {
            candidate.Invalidate(
                $"Profile '{profile.Name}' has no resource rule for SDK kind {component.Sdk.Kind}.");
            return;
        }

        foreach (ResourcePurpose purpose in required)
        {
            if (!component.Layout.Resources.Any(
                resource => resource.Purpose == purpose))
            {
                candidate.Invalidate(
                    $"{component.Role} SDK {component.Sdk.Kind} has no required {purpose} resource.");
            }
        }
    }

    private static void ValidateResourceOrder(
        ToolchainCandidate candidate,
        ResolvedSdkComponent component)
    {
        IReadOnlyList<Resource> resources = component.Layout.Resources;
        if (component.Sdk.Kind is SdkKind.Linux
            or SdkKind.Sysroot
            or SdkKind.AndroidNdk
            or SdkKind.WasiSdk)
        {
            foreach (ResourcePurpose purpose in new[]
            {
                ResourcePurpose.CInclude,
                ResourcePurpose.CppInclude,
            })
            {
                (Resource Resource, int Index)[] directories = resources
                    .Select((resource, index) => (resource, index))
                    .Where(item => item.resource.Purpose == purpose)
                    .Select(item => (item.resource, item.index))
                    .ToArray();
                foreach ((Resource general, int generalIndex) in directories)
                {
                    foreach ((Resource specific, int specificIndex) in directories)
                    {
                        if (specificIndex > generalIndex
                            && IsStrictDescendant(general.Path, specific.Path))
                        {
                            candidate.Invalidate(
                                $"{component.Role} SDK places target-specific {purpose} "
                                + $"'{specific.Path}' after its general directory '{general.Path}'.");
                            return;
                        }
                    }
                }
            }
        }

        if (component.Sdk.Kind == SdkKind.Msvc)
        {
            ValidateAtlMfcOrder(
                candidate, component.Role, resources, ResourcePurpose.CInclude);
            ValidateAtlMfcOrder(
                candidate, component.Role, resources, ResourcePurpose.CppInclude);
            ValidateAtlMfcOrder(
                candidate, component.Role, resources, ResourcePurpose.LibraryDirectory);
        }
        else if (component.Sdk.Kind == SdkKind.Windows)
        {
            ValidateSegmentOrder(
                candidate,
                component.Role,
                resources,
                ResourcePurpose.CInclude,
                ["ucrt", "shared", "um", "winrt", "cppwinrt"]);
            ValidateSegmentOrder(
                candidate,
                component.Role,
                resources,
                ResourcePurpose.CppInclude,
                ["ucrt", "shared", "um", "winrt", "cppwinrt"]);
            ValidateSegmentOrder(
                candidate,
                component.Role,
                resources,
                ResourcePurpose.LibraryDirectory,
                ["ucrt", "um"]);
        }
    }

    private static void ValidateAtlMfcOrder(
        ToolchainCandidate candidate,
        string role,
        IReadOnlyList<Resource> resources,
        ResourcePurpose purpose)
    {
        (Resource Resource, int Index)[] selected = resources
            .Select((resource, index) => (resource, index))
            .Where(item => item.resource.Purpose == purpose)
            .Select(item => (item.resource, item.index))
            .ToArray();
        int[] atlMfc = selected
            .Where(item => HasPathSegment(item.Resource.Path, "atlmfc"))
            .Select(item => item.Index)
            .ToArray();
        int[] ordinary = selected
            .Where(item => !HasPathSegment(item.Resource.Path, "atlmfc"))
            .Select(item => item.Index)
            .ToArray();
        if (atlMfc.Length > 0
            && ordinary.Length > 0
            && atlMfc.Min() < ordinary.Max())
        {
            candidate.Invalidate(
                $"{role} MSVC SDK places ATL/MFC {purpose} resources before ordinary resources.");
        }
    }

    private static void ValidateSegmentOrder(
        ToolchainCandidate candidate,
        string role,
        IReadOnlyList<Resource> resources,
        ResourcePurpose purpose,
        IReadOnlyList<string> expectedSegments)
    {
        int previous = -1;
        foreach (string segment in expectedSegments)
        {
            int[] indices = resources
                .Select((resource, index) => (resource, index))
                .Where(item => item.resource.Purpose == purpose
                    && HasPathSegment(item.resource.Path, segment))
                .Select(item => item.index)
                .ToArray();
            if (indices.Length == 0)
            {
                continue;
            }

            if (indices.Min() < previous)
            {
                candidate.Invalidate(
                    $"{role} Windows SDK {purpose} resources do not follow "
                    + $"{string.Join(", ", expectedSegments)} order.");
                return;
            }

            previous = indices.Max();
        }
    }

    private static bool HasPathSegment(string path, string expected) =>
        path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries)
            .Contains(expected, StringComparer.OrdinalIgnoreCase);

    private static bool IsStrictDescendant(string parent, string child) =>
        !PathIdentity.AreEqual(parent, child)
        && PathIdentity.Contains(parent, child);

    private static bool IsWithin(string root, string path) =>
        PathIdentity.Contains(root, path);

    private static void ValidateResource(
        ToolchainCandidate candidate,
        Sdk sdk,
        Resource resource)
    {
        ValidateAbsoluteExistingPath(
            candidate,
            resource.Path,
            $"SDK resource {resource.Purpose}",
            resource.IsDirectory);
        bool insideRoot = IsWithin(sdk.RootPath, resource.Path);
        bool insideEnvironment = IsWithin(sdk.EnvironmentPath, resource.Path);
        if (!resource.IsExternal && !insideRoot && !insideEnvironment)
        {
            candidate.Invalidate(
                $"Owned resource '{resource.Path}' lies outside the SDK identity paths.");
        }

        if (resource.IsExternal && insideRoot)
        {
            candidate.Invalidate(
                $"Resource '{resource.Path}' is marked external although it lies inside its SDK root.");
        }
    }

    private static void ValidateTool(
        AutoTestContext context,
        ToolchainCandidate candidate,
        Tool tool,
        string role)
    {
        ValidateAbsoluteExistingPath(
            candidate, tool.Path, role, expectDirectory: false);
        bool compatible = context.HostCapabilities.CanExecute(
            tool.HostArchitecture);
        if (!compatible)
        {
            candidate.Invalidate(
                $"{role} host architecture {tool.HostArchitecture} cannot run on "
                + $"the measured host architectures {string.Join(", ", context.HostCapabilities.Architectures)}.");
        }

        TargetArchitecture targetArchitecture = candidate.Toolchain!.TargetArchitecture;
        if (tool.TargetArchitecture != TargetArchitecture.Unknown
            && tool.TargetArchitecture != targetArchitecture)
        {
            candidate.Invalidate(
                $"{role} target architecture {tool.TargetArchitecture} differs from {targetArchitecture}.");
        }
    }

    private static void ValidateEmscriptenVariants(AutoTestContext context)
    {
        foreach (IGrouping<string, ToolchainCandidate> group in context.Candidates
            .Where(candidate => candidate.Toolchain?.AdapterKind == BuildAdapterKind.Emscripten
                && candidate.Status == CandidateStatus.Resolved)
            .GroupBy(candidate => candidate.InstallationIds[0], StringComparer.Ordinal))
        {
            ToolchainCandidate? defaultCandidate = group.FirstOrDefault(
                candidate => candidate.Toolchain!.Multilib == ".");
            ToolchainCandidate? picCandidate = group.FirstOrDefault(
                candidate => candidate.Toolchain!.Multilib is not null
                    && candidate.Toolchain.Multilib.Split('/').Contains(
                        "pic", StringComparer.OrdinalIgnoreCase));
            if (defaultCandidate is null || picCandidate is null)
            {
                continue;
            }

            HashSet<string> defaultLibraries = defaultCandidate.Toolchain!.Resources
                .Where(resource => resource.Purpose is ResourcePurpose.LibraryDirectory
                    or ResourcePurpose.RuntimeDirectory)
                .Select(resource => PathIdentity.Normalize(resource.Path))
                .ToHashSet(PathIdentity.Comparer);
            string[] overlap = picCandidate.Toolchain!.Resources
                .Where(resource => resource.Purpose is ResourcePurpose.LibraryDirectory
                    or ResourcePurpose.RuntimeDirectory)
                .Select(resource => PathIdentity.Normalize(resource.Path))
                .Where(defaultLibraries.Contains)
                .ToArray();
            if (overlap.Length > 0)
            {
                defaultCandidate.Invalidate(
                    "The Emscripten default layout shares variant library directories with pic.");
                picCandidate.Invalidate(
                    "The Emscripten pic layout shares variant library directories with default.");
            }
        }
    }

    private static void ValidateAbsoluteExistingPath(
        ToolchainCandidate candidate,
        string path,
        string description,
        bool? expectDirectory = null)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            candidate.Invalidate($"{description} path '{path}' is not absolute.");
            return;
        }

        bool fileExists = File.Exists(path);
        bool directoryExists = Directory.Exists(path);
        if (!fileExists && !directoryExists)
        {
            candidate.Invalidate($"{description} path '{path}' does not exist.");
            return;
        }

        if (expectDirectory == true && !directoryExists)
        {
            candidate.Invalidate($"{description} path '{path}' is not a directory.");
        }
        else if (expectDirectory == false && !fileExists)
        {
            candidate.Invalidate($"{description} path '{path}' is not a file.");
        }
    }

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

    private sealed class ResourceKeyComparer
        : IEqualityComparer<(ResourcePurpose Purpose, string Path, int? ApiLevel)>
    {
        internal static ResourceKeyComparer Instance { get; } = new();

        public bool Equals(
            (ResourcePurpose Purpose, string Path, int? ApiLevel) x,
            (ResourcePurpose Purpose, string Path, int? ApiLevel) y) =>
            x.Purpose == y.Purpose
            && PathIdentity.Comparer.Equals(x.Path, y.Path)
            && x.ApiLevel == y.ApiLevel;

        public int GetHashCode(
            (ResourcePurpose Purpose, string Path, int? ApiLevel) value) =>
            HashCode.Combine(
                value.Purpose,
                PathIdentity.Comparer.GetHashCode(value.Path),
                value.ApiLevel);
    }
}
