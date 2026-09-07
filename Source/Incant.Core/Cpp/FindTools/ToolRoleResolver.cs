using Incant.Base;
using Incant.Core.Cpp;

namespace Incant.Core.Cpp.FindTools;

/// <summary>Resolves one logical compiler-tool role from installation-local evidence.</summary>
internal static class ToolRoleResolver
{
    internal static async Task<Tool?> FindAsync(
        CompilerInstallation installation,
        string name,
        ToolQuery query,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RoleCandidate> candidates = await CandidatesAsync(
            installation,
            name,
            cancellationToken).ConfigureAwait(false);
        foreach (RoleCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await MatchesRoleAsync(
                installation,
                name,
                candidate,
                context,
                cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            TargetArchitecture host = await HostExecutableInspector.SelectAsync(
                candidate.Path,
                query.HostArchitecture,
                context,
                allowsLaunchers: true,
                cancellationToken).ConfigureAwait(false);
            if (query.HostArchitecture is not null
                && host == TargetArchitecture.Unknown)
            {
                continue;
            }

            return new Tool(name, candidate.Path, host);
        }

        return null;
    }

    private static async Task<IReadOnlyList<RoleCandidate>> CandidatesAsync(
        CompilerInstallation installation,
        string name,
        CancellationToken cancellationToken)
    {
        var candidates = new List<RoleCandidate>();
        string[] names = CandidateNames(installation, name).ToArray();
        foreach (string driverName in DriverQueries(installation, name))
        {
            foreach (string argument in DriverArguments(
                installation.Family,
                driverName))
            {
                ProcessResult? result =
                    await installation.Probe.RunAsync(
                        [argument],
                        cancellationToken).ConfigureAwait(false);
                string? path = ReportedPath(
                    result,
                    installation.BinPath);
                if (path is not null)
                {
                    candidates.Add(new RoleCandidate(
                        path,
                        RoleEvidence.Driver,
                        IsPrivate(path, installation),
                        DirectoryRank: -1,
                        NameRank: Array.IndexOf(names, driverName)));
                }
            }
        }

        for (int directoryRank = 0;
            directoryRank < installation.SearchDirectories.Count;
            directoryRank++)
        {
            CompilerSearchDirectory directory =
                installation.SearchDirectories[directoryRank];
            for (int nameRank = 0; nameRank < names.Length; nameRank++)
            {
                string? path = SearchPaths.Executable(
                    directory.Path,
                    names[nameRank],
                    wrappers: true);
                if (path is not null)
                {
                    candidates.Add(new RoleCandidate(
                        path,
                        RoleEvidence.FileName,
                        IsPrivate(path, installation),
                        directoryRank,
                        nameRank));
                }
            }
        }

        return candidates
            .GroupBy(candidate => candidate.Path, SearchPaths.Comparer)
            .Select(group => group
                .OrderBy(candidate => candidate.Evidence)
                .ThenBy(candidate => candidate.DirectoryRank)
                .ThenBy(candidate => candidate.NameRank)
                .First())
            .OrderBy(candidate => candidate.Evidence)
            .ThenBy(candidate => candidate.DirectoryRank)
            .ThenBy(candidate => candidate.NameRank)
            .ThenBy(
                candidate => SearchPaths.InvocationIdentity(candidate.Path),
                SearchPaths.Comparer)
            .ThenBy(candidate => candidate.Path, SearchPaths.Comparer)
            .ToArray();
    }

    private static IEnumerable<string> CandidateNames(
        CompilerInstallation installation,
        string role)
    {
        var names = new List<string>();
        string[] roleNames = DriverRoleNames(
            installation.Family,
            role).ToArray();
        if (installation.Family == CompilerFamily.Gnu
            && role is "ar" or "ranlib" or "ld"
            && installation.DefaultTargetTriple is string targetTriple)
        {
            AddName(names, targetTriple + "-" + role);
        }

        foreach (string alias in installation.Aliases)
        {
            CompilerName? identity = CompilerName.Parse(alias);
            if (identity is null)
            {
                if (SearchPaths.Comparer.Equals(
                    alias,
                    installation.InvocationPath)
                    && CanUsePrimaryDriver(
                        installation.Family,
                        role))
                {
                    AddName(
                        names,
                        Path.GetFileName(alias));
                }

                continue;
            }

            if (IsCompatibleDriver(
                installation.Family,
                role,
                identity.Driver))
            {
                AddName(
                    names,
                    CompilerName.ExecutableStem(alias));
            }

            foreach (string roleName in roleNames)
            {
                AddName(
                    names,
                    identity.Prefix + roleName + identity.VersionSuffix);
                AddName(names, identity.Prefix + roleName);
            }
        }

        Version? version = installation.Version;
        if (version is not null)
        {
            foreach (string roleName in roleNames)
            {
                AddName(
                    names,
                    roleName + "-" + version.Major + "." + version.Minor);
                AddName(names, roleName + "-" + version.Major);
                if (installation.Family == CompilerFamily.Llvm
                    && IsLlvmTool(roleName))
                {
                    AddName(names, roleName + version.Major);
                }
            }
        }

        foreach (string roleName in roleNames)
        {
            AddName(names, roleName);
        }

        return names;
    }

    private static IEnumerable<string> DriverQueries(
        CompilerInstallation installation,
        string role)
    {
        if (IsDriver(role))
        {
            yield break;
        }

        yield return role;
        if (installation.Family == CompilerFamily.Gnu
            && role is "ar" or "ranlib" or "ld"
            && installation.DefaultTargetTriple is string triple)
        {
            yield return triple + "-" + role;
        }
    }

    private static IEnumerable<string> DriverArguments(
        CompilerFamily family,
        string name)
    {
        if (family != CompilerFamily.Gnu)
        {
            yield return "--print-prog-name=" + name;
        }

        yield return "-print-prog-name=" + name;
    }

    private static async Task<bool> MatchesRoleAsync(
        CompilerInstallation installation,
        string role,
        RoleCandidate candidate,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        if (!IsManagedRole(installation.Family, role))
        {
            return true;
        }

        if (IsDriver(role))
        {
            return await MatchesDriverAsync(
                installation,
                candidate.Path,
                context,
                cancellationToken).ConfigureAwait(false);
        }

        if (installation.Family == CompilerFamily.Llvm
            && IsLlvmTool(role))
        {
            return candidate.IsPrivate
                || await MatchesLlvmVersionAsync(
                    installation,
                    candidate.Path,
                    context,
                    cancellationToken).ConfigureAwait(false);
        }

        if (installation.Family == CompilerFamily.Gnu
            && role is "gcc-ar" or "gcc-ranlib")
        {
            return candidate.Evidence == RoleEvidence.Driver
                || candidate.IsPrivate
                || HasCompilerVersionSuffix(
                    candidate.Path,
                    installation.Version);
        }

        if (installation.Family == CompilerFamily.Gnu
            && role is "ar" or "ranlib" or "ld")
        {
            return candidate.Evidence == RoleEvidence.Driver
                || candidate.IsPrivate
                || HasTargetPrefix(
                    candidate.Path,
                    installation.DefaultTargetTriple);
        }

        return candidate.IsPrivate
            || candidate.Evidence == RoleEvidence.Driver;
    }

    private static async Task<bool> MatchesDriverAsync(
        CompilerInstallation installation,
        string path,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        CompilerProbe? probe = await CompilerProbe.OpenAsync(
            path,
            context,
            cancellationToken).ConfigureAwait(false);
        if (probe is null)
        {
            return false;
        }

        CompilerFamily family = probe.Family;
        return family == installation.Family
            && probe.Version == installation.Version
            && TargetIdentity.AreEquivalent(
                probe.DefaultTarget?.Triple,
                installation.DefaultTargetTriple);
    }

    private static async Task<bool> MatchesLlvmVersionAsync(
        CompilerInstallation installation,
        string path,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        if (installation.Version is null)
        {
            return false;
        }

        ProcessResult? result = await context.ProbeAsync(
            path,
            ["--version"],
            cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return false;
        }

        string text = result.StandardOutput + result.StandardError;
        if (!text.Contains("LLVM", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("LLD", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("clang", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Version? version = SearchPaths.Version(text);
        return version?.Major == installation.Version.Major;
    }

    private static string? ReportedPath(
        ProcessResult? result,
        string compilerDirectory)
    {
        string? value = result?.StandardOutput
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(
                value,
                Path.GetFileName(value),
                StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            string path = Path.IsPathFullyQualified(value)
                ? value
                : Path.GetFullPath(value, compilerDirectory);
            return File.Exists(path)
                ? path
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsPrivate(
        string path,
        CompilerInstallation installation)
    {
        string canonical = SearchPaths.InvocationIdentity(path);
        return installation.SearchDirectories.Any(directory =>
            directory.IsPrivate
            && SearchPaths.Contains(
                SearchPaths.InvocationIdentity(directory.Path),
                canonical));
    }

    private static bool HasCompilerVersionSuffix(
        string path,
        Version? version)
    {
        if (version is null)
        {
            return false;
        }

        string stem = CompilerName.ExecutableStem(path);
        return stem.EndsWith(
            "-" + version.Major,
            StringComparison.Ordinal)
            || stem.EndsWith(
                "-" + version.Major + "." + version.Minor,
                StringComparison.Ordinal);
    }

    private static bool HasTargetPrefix(
        string path,
        string? triple)
    {
        if (triple is null)
        {
            return false;
        }

        string stem = CompilerName.ExecutableStem(path);
        if (stem.StartsWith(triple + "-", StringComparison.Ordinal))
        {
            return true;
        }

        string canonicalTriple = new TargetIdentity(triple).Canonical;
        int separator = stem.LastIndexOf("-", StringComparison.Ordinal);
        return separator > 0
            && TargetIdentity.AreEquivalent(
                stem[..separator],
                canonicalTriple);
    }

    private static bool IsDriver(string role) => CompilerName.IsDriver(role);

    private static bool CanUsePrimaryDriver(
        CompilerFamily family,
        string role) => family == CompilerFamily.Gnu
        ? role is "gcc" or "g++" or "cc" or "c++"
        : role is "clang" or "clang++" or "cc" or "c++";

    private static IEnumerable<string> DriverRoleNames(
        CompilerFamily family,
        string role)
    {
        yield return role;
        string? alternate = (family, role) switch
        {
            (CompilerFamily.Gnu, "gcc") => "cc",
            (CompilerFamily.Gnu, "cc") => "gcc",
            (CompilerFamily.Gnu, "g++") => "c++",
            (CompilerFamily.Gnu, "c++") => "g++",
            (CompilerFamily.Llvm or CompilerFamily.AppleClang, "clang") => "cc",
            (CompilerFamily.Llvm or CompilerFamily.AppleClang, "cc") => "clang",
            (CompilerFamily.Llvm or CompilerFamily.AppleClang, "clang++") => "c++",
            (CompilerFamily.Llvm or CompilerFamily.AppleClang, "c++") => "clang++",
            _ => null,
        };
        if (alternate is not null)
        {
            yield return alternate;
        }
    }

    private static bool IsCompatibleDriver(CompilerFamily family, string requested, string actual)
    {
        if (requested == "clang-cl")
        {
            return family != CompilerFamily.Gnu && actual == "clang-cl";
        }

        bool requestedCpp = requested is "g++" or "c++" or "clang++";
        bool actualCpp = actual is "g++" or "c++" or "clang++";
        return CanUsePrimaryDriver(family, requested)
            && actual != "clang-cl" && requestedCpp == actualCpp;
    }

    private static bool IsManagedRole(
        CompilerFamily family,
        string role) => IsDriver(role)
        || family == CompilerFamily.Llvm
            && (IsLlvmTool(role)
                || role is "ar" or "ranlib" or "ld")
        || family == CompilerFamily.Gnu
            && role is "gcc-ar" or "gcc-ranlib"
                or "ar" or "ranlib" or "ld";

    private static bool IsLlvmTool(string role) => role is
        "llvm-ar"
        or "llvm-ranlib"
        or "llvm-lib"
        or "ld.lld"
        or "lld-link"
        or "wasm-ld";

    private static void AddName(
        ICollection<string> names,
        string name)
    {
        if (!names.Contains(name, StringComparer.Ordinal))
        {
            names.Add(name);
        }
    }

    private sealed record RoleCandidate(
        string Path,
        RoleEvidence Evidence,
        bool IsPrivate,
        int DirectoryRank,
        int NameRank);

    private enum RoleEvidence
    {
        Driver,
        FileName,
    }
}
