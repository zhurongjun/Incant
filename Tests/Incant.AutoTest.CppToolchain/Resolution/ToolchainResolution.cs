using Incant.Base;
using Incant.Core.Cpp;
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

internal static class ToolchainResolution
{
    internal static async Task<Sdk?> FindCompilerSdkAsync(
        AutoTestContext context,
        InstallationDiscovery owner,
        ToolSet toolSet,
        SdkKind kind,
        TargetPlatform platform,
        TargetArchitecture architecture,
        string? triple,
        string? multilib,
        string? sysrootPath,
        CancellationToken cancellationToken)
    {
        var query = new SdkQuery
        {
            Kind = kind,
            RootPath = owner.Manifest.RootPath,
            CompilerPath = toolSet.CompilerPath,
            TargetPlatform = platform,
            TargetArchitecture = architecture,
            TargetTriple = triple,
            Multilib = multilib,
            SysrootPath = sysrootPath,
            IncludePreview = true,
            Environment = context.EnvironmentFor(owner.Manifest),
        };
        query = owner.Requirement.SdkVersion?.Apply(query) ?? query;
        DiscoveryProbe probe = await DiscoveryStage.RunSdkProbeAsync(
            context,
            CreateId(
                owner.Requirement.Id,
                "resolve",
                toolSet.Kind,
                toolSet.Version,
                toolSet.CompilerVersion,
                platform,
                architecture,
                multilib ?? "default"),
            "resolve target-specific compiler SDK",
            SdkFinder.CreateDefault(),
            query,
            cancellationToken).ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            return null;
        }

        return probe.Sdks
            .Where(sdk => sdk.Kind == kind
                && CompilerMatches(toolSet, sdk)
                && BelongsTo(owner.Manifest, sdk)
                && (kind != SdkKind.Msvc || MsvcIdentityMatches(toolSet, sdk)))
            .OrderByDescending(sdk => sdk.Version)
            .FirstOrDefault();
    }

    internal static void EnsureUniqueCandidateIds(AutoTestContext context)
    {
        string[] duplicateIds = context.Candidates
            .GroupBy(candidate => candidate.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateIds.Length > 0)
        {
            throw new InvalidOperationException(
                "Resolved candidate ids must be unique: "
                + string.Join(", ", duplicateIds));
        }
    }

    internal static void EnsureCoverage(AutoTestContext context)
    {
        foreach (InstallationRequirement requirement in context.Profile.Installations
            .Where(requirement => requirement.Required))
        {
            bool resolved = context.Candidates.Any(candidate =>
                Covers(requirement, context, candidate));
            if (!resolved)
            {
                AddInvalid(
                    context,
                    requirement.Id + "-no-resolved-candidate",
                    [requirement.Id],
                    "No complete build candidate was resolved for this required installation.");
            }
        }
    }

    private static bool Covers(
        InstallationRequirement requirement,
        AutoTestContext context,
        ToolchainCandidate candidate)
    {
        if (candidate.Status != CandidateStatus.Resolved
            || candidate.Toolchain is not ResolvedToolchain toolchain)
        {
            return false;
        }

        InstallationManifest manifest = context.Installations
            .Single(installation => installation.Requirement.Id == requirement.Id)
            .Manifest;
        bool HasToolSet(ToolKind kind) =>
            toolchain.ToolSet.Kind == kind
            && BelongsTo(manifest, toolchain.ToolSet);
        bool HasSdk(SdkKind kind) => toolchain.Sdks.Any(component =>
            component.Sdk.Kind == kind
            && BelongsTo(manifest, component.Sdk));
        return requirement.Kind switch
        {
            InstallationKind.VisualStudio => HasToolSet(ToolKind.VisualStudio)
                && HasSdk(SdkKind.Msvc),
            InstallationKind.WindowsSdk => HasSdk(SdkKind.Windows),
            InstallationKind.Gnu => HasToolSet(ToolKind.Gnu) && HasSdk(SdkKind.Gnu),
            InstallationKind.Llvm => HasToolSet(ToolKind.Llvm) && HasSdk(SdkKind.Llvm),
            InstallationKind.Xcode => HasToolSet(ToolKind.Xcode)
                && HasSdk(SdkKind.AppleClang)
                && HasSdk(SdkKind.Apple),
            InstallationKind.AndroidNdk => HasToolSet(ToolKind.AndroidNdk)
                && HasSdk(SdkKind.AndroidNdk),
            InstallationKind.Emscripten => HasToolSet(ToolKind.Emscripten)
                && HasSdk(SdkKind.Emscripten),
            InstallationKind.WasiSdk => HasToolSet(ToolKind.WasiSdk)
                && HasSdk(SdkKind.WasiSdk),
            _ => throw new ArgumentOutOfRangeException(
                nameof(requirement), requirement.Kind, null),
        };
    }

    internal static bool BelongsTo(
        InstallationManifest manifest,
        ToolSet toolSet) =>
        Related(manifest.RootPath, toolSet.RootPath)
        || Related(manifest.RootPath, toolSet.EnvironmentPath)
        || toolSet.CompilerPath is not null
            && Related(manifest.RootPath, toolSet.CompilerPath);

    internal static bool BelongsTo(
        InstallationManifest manifest,
        Sdk sdk) =>
        Related(manifest.RootPath, sdk.RootPath)
        || Related(manifest.RootPath, sdk.EnvironmentPath)
        || sdk.CompilerPath is not null
            && Related(manifest.RootPath, sdk.CompilerPath);

    internal static void AddInvalid(
        AutoTestContext context,
        string id,
        IEnumerable<string> installationIds,
        string reason)
    {
        if (context.Candidates.Any(candidate => candidate.Id == id))
        {
            return;
        }

        var candidate = new ToolchainCandidate(id, installationIds);
        candidate.Invalidate(reason);
        context.Candidates.Add(candidate);
    }

    internal static bool RequireTools(
        ToolchainCandidate candidate,
        params (Tool? Tool, string Name)[] requirements)
    {
        bool allFound = true;
        foreach ((Tool? tool, string name) in requirements)
        {
            if (tool is not null)
            {
                continue;
            }

            candidate.Invalidate($"Required tool '{name}' was not resolved.");
            allFound = false;
        }

        return allFound;
    }

    internal static async Task<Tool?> FindToolAsync(
        ToolchainCandidate candidate,
        ToolSet toolSet,
        string name,
        ToolQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await toolSet.FindToolAsync(
                name, query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            candidate.Invalidate(
                $"Tool '{name}' lookup failed in '{toolSet.RootPath}': {exception.Message}");
            return null;
        }
    }

    internal static async Task<Tool?> FindAnyToolAsync(
        ToolchainCandidate candidate,
        ToolSet toolSet,
        IReadOnlyList<string> names,
        ToolQuery query,
        CancellationToken cancellationToken)
    {
        foreach (string name in names)
        {
            Tool? tool = await FindToolAsync(
                candidate, toolSet, name, query, cancellationToken).ConfigureAwait(false);
            if (tool is not null || candidate.Status == CandidateStatus.Invalid)
            {
                return tool;
            }
        }

        return null;
    }

    internal static ToolQuery Query(
        AutoTestContext context,
        TargetPlatform platform,
        TargetArchitecture architecture,
        bool constrainHost = true) => new()
        {
            HostArchitecture = constrainHost
                ? context.Profile.HostArchitecture
                : null,
            TargetPlatform = platform,
            TargetArchitecture = architecture,
        };

    internal static TargetLayout? FindLayout(
        Sdk sdk,
        TargetPlatform platform,
        TargetArchitecture architecture,
        string? triple = null,
        string? multilib = null)
    {
        IEnumerable<TargetLayout> layouts = sdk.Layouts.Where(layout =>
            layout.Platform == platform && layout.Architecture == architecture);
        if (triple is not null)
        {
            layouts = layouts.Where(layout => layout.TargetTriple is not null
                && TargetTripleIdentity.AreEquivalent(layout.TargetTriple, triple));
        }

        if (multilib is not null)
        {
            layouts = layouts.Where(layout => layout.Multilib == multilib);
        }

        return layouts.FirstOrDefault();
    }

    internal static Sdk? FindMsvcSdk(
        ToolSet toolSet,
        IEnumerable<Sdk> sdks) => sdks
            .Where(sdk => MsvcIdentityMatches(toolSet, sdk))
            .SingleOrDefault();

    internal static bool MsvcIdentityMatches(ToolSet toolSet, Sdk sdk) =>
        toolSet.Kind == ToolKind.VisualStudio
        && sdk.Kind == SdkKind.Msvc
        && SamePath(toolSet.RootPath, sdk.RootPath)
        && SamePath(toolSet.EnvironmentPath, sdk.EnvironmentPath)
        && Equals(toolSet.Version, sdk.Version);

    internal static bool CompilerMatches(ToolSet toolSet, Sdk sdk) =>
        toolSet.CompilerPath is not null
        && sdk.CompilerPath is not null
        && SamePath(toolSet.CompilerPath, sdk.CompilerPath);

    internal static IEnumerable<InstallationDiscovery> Installations(
        AutoTestContext context,
        InstallationKind kind) => context.Installations.Where(
            installation => installation.Requirement.Kind == kind);

    internal static IEnumerable<SdkOwner> OwnedSdks(
        AutoTestContext context,
        InstallationKind installationKind,
        SdkKind sdkKind) => Installations(context, installationKind)
            .SelectMany(owner => owner.Sdks
                .Where(sdk => sdk.Kind == sdkKind)
                .Select(sdk => new SdkOwner(owner, sdk)));

    internal static IReadOnlyDictionary<string, string?> MergeEnvironments(
        AutoTestContext context,
        params InstallationManifest[] installations)
    {
        var result = new Dictionary<string, string?>(
            context.BaseEnvironment,
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        foreach (InstallationManifest installation in installations)
        {
            foreach ((string name, string? value) in installation.Environment)
            {
                result[name] = value;
            }
        }

        return result;
    }

    internal static bool CanRunNative(
        AutoTestContext context,
        TargetPlatform platform,
        TargetArchitecture architecture,
        string? multilib = null)
    {
        if (!context.Profile.SupportsExecution(ExecutionMode.Native)
            || string.Equals(multilib, "x32", StringComparison.Ordinal))
        {
            return false;
        }

        TargetPlatform hostPlatform = context.Profile.HostOS switch
        {
            PlatformOS.Windows => TargetPlatform.Windows,
            PlatformOS.Linux => TargetPlatform.Linux,
            PlatformOS.OSX => TargetPlatform.MacOS,
            _ => TargetPlatform.Unknown,
        };
        return platform == hostPlatform
            && (architecture == context.Profile.HostArchitecture
                || context.Profile.HostOS == PlatformOS.Windows
                    && context.Profile.HostArchitecture == TargetArchitecture.X64
                    && architecture == TargetArchitecture.X86
                || context.Profile.HostOS == PlatformOS.Linux
                    && context.Profile.HostArchitecture == TargetArchitecture.X64
                    && architecture == TargetArchitecture.X86);
    }

    internal static string WindowsTriple(TargetArchitecture architecture) =>
        architecture switch
        {
            TargetArchitecture.X64 => "x86_64-pc-windows-msvc",
            TargetArchitecture.ARM => "armv7-pc-windows-msvc",
            TargetArchitecture.ARM64 => "aarch64-pc-windows-msvc",
            TargetArchitecture.X86 => "i386-pc-windows-msvc",
            _ => throw new ArgumentOutOfRangeException(
                nameof(architecture), architecture, null),
        };

    internal static string CreateId(string prefix, params object?[] parts)
    {
        string value = string.Join(
            "-",
            new[] { prefix }.Concat(parts
                .Where(part => part is not null)
                .Select(part => part!.ToString()!)));
        var characters = value.Select(character =>
            char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
        return string.Join(
            '-',
            new string(characters.ToArray())
                .Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    internal static bool Related(string left, string right) =>
        PathIdentity.Related(left, right);

    internal static bool SamePath(string left, string right) =>
        PathIdentity.AreEqual(left, right);
}

internal sealed record ToolSetOwner(
    InstallationDiscovery Owner,
    ToolSet ToolSet);

internal sealed record SdkOwner(
    InstallationDiscovery Owner,
    Sdk Sdk);
