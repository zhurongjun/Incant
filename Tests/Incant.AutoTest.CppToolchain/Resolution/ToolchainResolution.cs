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
        DriverConfiguration driver,
        CancellationToken cancellationToken)
    {
        var query = new SdkQuery
        {
            Kind = kind,
            RootPath = owner.Manifest.RootPath,
            CompilerPath = toolSet.CompilerPath,
            TargetPlatform = platform,
            TargetArchitecture = architecture,
            TargetTriple = driver.TargetTriple,
            Multilib = driver.Multilib,
            SysrootPath = driver.SysrootPath,
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
                driver.Multilib ?? "default"),
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

    internal static bool BelongsTo(InstallationManifest manifest, ToolSet toolSet) =>
        InstallationIdentity.Contains(manifest, toolSet);

    internal static bool BelongsTo(InstallationManifest manifest, Sdk sdk) =>
        InstallationIdentity.Contains(manifest, sdk);

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

        var candidate = new ToolchainCandidate(id, installationIds,
            context.Installations.Any(owner => owner.Managed && owner.Requirement.Required
                && installationIds.Contains(owner.Requirement.Id)));
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
        AutoTestContext context,
        ToolchainCandidate candidate,
        ToolSet toolSet,
        string name,
        ToolQuery query,
        CancellationToken cancellationToken)
    {
        IEnumerable<ToolQuery> queries =
            query.HostArchitecture is null
                ? [query]
                : context.HostCapabilities.Architectures.Select(
                    architecture => query with
                    {
                        HostArchitecture = architecture,
                    }).Append(query with { HostArchitecture = null });
        foreach (ToolQuery hostQuery in queries)
        {
            try
            {
                Tool? tool = await toolSet.FindToolAsync(
                    name,
                    hostQuery,
                    cancellationToken).ConfigureAwait(false);
                if (tool is not null
                    && (hostQuery.HostArchitecture is not null
                        || tool.HostArchitecture == TargetArchitecture.Unknown
                        || context.HostCapabilities.Architectures.Contains(tool.HostArchitecture)))
                {
                    return tool;
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                candidate.Decisions.Add(
                    $"Tool '{name}' lookup failed in '{toolSet.RootPath}': "
                    + exception.Message);
                return null;
            }
        }

        return null;
    }

    internal static async Task<Tool?> FindAnyToolAsync(
        AutoTestContext context,
        ToolchainCandidate candidate,
        ToolSet toolSet,
        IReadOnlyList<string> names,
        ToolQuery query,
        CancellationToken cancellationToken)
    {
        foreach (string name in names)
        {
            Tool? tool = await FindToolAsync(
                context,
                candidate,
                toolSet,
                name,
                query,
                cancellationToken).ConfigureAwait(false);
            if (tool is not null
                || candidate.Status == CandidateStatus.Invalid)
            {
                return tool;
            }
        }

        return null;
    }

    internal static ToolQuery Query(
        AutoTestContext context,
        TargetPlatform platform,
        TargetArchitecture architecture) => new()
        {
            HostArchitecture = context.HostCapabilities.Architectures[0],
            TargetPlatform = platform,
            TargetArchitecture = architecture,
        };

    internal static TargetLayout? FindLayout(
        Sdk sdk,
        TargetPlatform platform,
        TargetArchitecture architecture,
        string? multilib = null)
    {
        IEnumerable<TargetLayout> layouts = sdk.Layouts.Where(layout =>
            layout.Platform == platform && layout.Architecture == architecture);
        if (multilib is not null)
        {
            layouts = layouts.Where(layout => layout.Multilib == multilib);
        }

        return layouts.FirstOrDefault();
    }

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
            && context.HostCapabilities.Architectures.Contains(architecture);
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

internal sealed record SdkOwner(
    InstallationDiscovery Owner,
    Sdk Sdk);
