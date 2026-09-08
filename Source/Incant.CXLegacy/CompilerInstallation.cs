namespace Incant.CXLegacy;

internal enum CompilerFamily
{
    Gnu,
    Llvm,
    AppleClang,
}

internal sealed record CompilerSearchDirectory(string Path, bool IsPrivate);

internal sealed record CompilerInspectionFailure(
    string Path,
    string Message,
    IReadOnlyList<Source> Sources,
    bool TimedOut = false,
    DiagnosticSeverity Severity = DiagnosticSeverity.Warning);

internal sealed record CompilerDiscoveryResult(
    IReadOnlyList<CompilerInstallation> Installations,
    IReadOnlyList<CompilerInspectionFailure> Failures);

internal sealed class CompilerInstallation
{
    private CompilerInstallation(
        CompilerInvocationCandidate primary,
        CompilerProbe probe,
        CompilerFamily family,
        IReadOnlyList<string> aliases,
        IReadOnlyList<CompilerSearchDirectory> searchDirectories,
        IReadOnlyList<Source> sources,
        CompilerMetadata metadata)
    {
        InvocationPath = primary.InvocationPath;
        CanonicalPath = probe.ResolvedPath;
        EnvironmentPath = metadata.EnvironmentPath;
        ProductVersion = metadata.ProductVersion;
        Channel = metadata.Channel;
        Probe = probe;
        Family = family;
        Aliases = aliases;
        SearchDirectories = searchDirectories;
        Sources = sources;
    }

    internal string InvocationPath { get; }

    internal string CanonicalPath { get; }

    internal string BinPath => Path.GetDirectoryName(InvocationPath)!;

    internal string EnvironmentPath { get; }

    internal CompilerProbe Probe { get; }

    internal CompilerFamily Family { get; }

    internal Version? Version => Probe.Version;

    internal string? DefaultTargetTriple => Probe.DefaultTarget?.Triple;

    internal Channel Channel { get; }

    internal Version? ProductVersion { get; }

    internal IReadOnlyList<string> Aliases { get; }

    internal IReadOnlyList<CompilerSearchDirectory> SearchDirectories { get; }

    internal IReadOnlyList<Source> Sources { get; }

    internal static async Task<CompilerDiscoveryResult> DiscoverAsync(
        string? explicitRoot,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CompilerInvocationCandidate> candidates =
            await CompilerLocator.FindAsync(
                explicitRoot,
                context,
                cancellationToken).ConfigureAwait(false);
        return await InspectCandidatesAsync(candidates, context, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<CompilerDiscoveryResult> InspectCandidatesAsync(
        IEnumerable<CompilerInvocationCandidate> candidates,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        CompilerInvocationCandidate[] entries = CompilerInvocationCandidate.Merge(candidates).ToArray();
        var inspections = new CompilerInspection[entries.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, entries.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                inspections[index] = await InspectAsync(entries[index], context, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        var failures = new List<CompilerInspectionFailure>();
        for (int index = 0; index < inspections.Length; index++)
        {
            CompilerInspection inspection = inspections[index];
            if (inspection.Failure is not CompilerInspectionFailure failure)
            {
                continue;
            }

            failures.Add(failure);
            if (failure.TimedOut)
            {
                CompilerInspection retry = await InspectAsync(entries[index], context, cancellationToken)
                    .ConfigureAwait(false);
                inspections[index] = retry;
                if (retry.Failure is not null)
                {
                    failures.Add(retry.Failure with { Message = "Identity retry: " + retry.Failure.Message });
                }
                else if (retry.Probe is not null)
                {
                    failures.Add(new CompilerInspectionFailure(entries[index].InvocationPath,
                        "The compiler identity was recovered by one serial retry after a timeout.",
                        entries[index].Sources));
                }
            }
        }

        var groups = new List<List<CompilerInspection>>();
        foreach (CompilerInspection inspection in inspections.Where(item => item.Probe is not null))
        {
            failures.AddRange(inspection.Probe!.IdentityDiagnostics.Select(outcome =>
                new CompilerInspectionFailure(inspection.Candidate.InvocationPath,
                    outcome.Describe(), inspection.Candidate.Sources)));
            List<CompilerInspection>? group = groups.FirstOrDefault(items => CanJoin(items, inspection));
            if (group is null)
            {
                group = [];
                groups.Add(group);
            }

            group.Add(inspection);
        }

        return new CompilerDiscoveryResult(groups.Select(Create).ToArray(), failures.Distinct().ToArray());
    }

    private static bool CanJoin(IReadOnlyList<CompilerInspection> group, CompilerInspection candidate)
    {
        CompilerProbe probe = candidate.Probe!;
        CompilerProbe first = group[0].Probe!;
        if (probe.Family != first.Family || probe.Version != first.Version
            || probe.DefaultTarget?.Canonical != first.DefaultTarget?.Canonical)
        {
            return false;
        }

        if (group.Any(item => SearchPaths.Comparer.Equals(item.Probe!.ResolvedPath, probe.ResolvedPath)))
        {
            return true;
        }

        CompilerName? name = CompilerName.Parse(candidate.Candidate.InvocationPath);
        if (name is null || !name.IsTargetQualified(probe.DefaultTarget))
        {
            return false;
        }

        // A companion can complete one driver pair; it must not bridge unrelated C entries.
        if (group.Any(item => CompilerName.Parse(item.Candidate.InvocationPath)?.DriverRank == name.DriverRank))
        {
            return false;
        }

        return group.Any(item =>
        {
            CompilerName? other = CompilerName.Parse(item.Candidate.InvocationPath);
            return other is not null && name.IsCompanionOf(other)
                && other.IsTargetQualified(item.Probe!.DefaultTarget)
                && SearchPaths.Comparer.Equals(
                    Path.GetDirectoryName(item.Probe!.ResolvedPath),
                    Path.GetDirectoryName(probe.ResolvedPath));
        });
    }

    private static async Task<CompilerInspection> InspectAsync(
        CompilerInvocationCandidate candidate,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            CompilerOpenResult opened = await CompilerProbe.OpenDetailedAsync(
                candidate.InvocationPath,
                context,
                cancellationToken).ConfigureAwait(false);
            CompilerProbe? probe = opened.Probe;
            if (probe is null)
            {
                return new CompilerInspection(
                    candidate,
                    null,
                    new CompilerInspectionFailure(
                        candidate.InvocationPath,
                        opened.Error ?? opened.Failure?.Describe() ?? "The compiler identity could not be established.",
                        candidate.Sources,
                        opened.Failure?.Status == ProbeStatus.TimedOut));
            }

            if (probe.DefaultTarget?.Triple.Contains(
                "mingw",
                StringComparison.OrdinalIgnoreCase) == true)
            {
                return new CompilerInspection(candidate, null, null);
            }

            if (candidate.DiscoveryAnchor == CompilerDiscoveryAnchor.Directory
                && CompilerName.Parse(candidate.InvocationPath)?.IsTargetQualified(probe.DefaultTarget) is not true)
            {
                return new CompilerInspection(candidate, null,
                    new CompilerInspectionFailure(candidate.InvocationPath,
                        "Skipped automatic entry: its name prefix does not identify the reported target "
                        + probe.DefaultTarget?.Triple + ".",
                        candidate.Sources, Severity: DiagnosticSeverity.Info));
            }

            CompilerMetadata metadata = await InspectMetadataAsync(candidate, probe, context, cancellationToken)
                .ConfigureAwait(false);
            return new CompilerInspection(candidate, probe, null, metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return new CompilerInspection(
                candidate,
                null,
                new CompilerInspectionFailure(
                    candidate.InvocationPath,
                    exception.Message,
                    candidate.Sources));
        }
    }

    private static CompilerInstallation Create(
        IEnumerable<CompilerInspection> group)
    {
        CompilerInspection[] inspections = group.ToArray();
        IOrderedEnumerable<CompilerInspection> ordered = inspections
            .OrderBy(inspection => inspection.Candidate.Sources.Min())
            .ThenBy(inspection => inspection.Candidate.DiscoveryAnchor)
            .ThenBy(inspection => DriverRank(inspection.Candidate.InvocationPath))
            .ThenBy(inspection => InvocationRank(
                inspection.Candidate.InvocationPath))
            .ThenBy(
                inspection => inspection.Candidate.InvocationPath,
                SearchPaths.Comparer);
        CompilerInspection preferred = ordered.First();
        var aliases = new List<string>();
        var searchDirectories = new List<CompilerSearchDirectory>();
        foreach (CompilerInspection inspection in ordered)
        {
            AddPath(aliases, inspection.Candidate.InvocationPath);
            string invocationDirectory = Path.GetDirectoryName(
                inspection.Candidate.InvocationPath)!;
            AddDirectory(
                searchDirectories,
                invocationDirectory,
                inspection.Candidate.IsPrivateDirectory);
            string canonicalDirectory = Path.GetDirectoryName(
                inspection.Probe!.ResolvedPath)!;
            AddDirectory(
                searchDirectories,
                canonicalDirectory,
                !CompilerLocator.IsSharedDirectory(canonicalDirectory));
            foreach (CompilerSearchDirectory directory
                in inspection.Candidate.AssociatedSearchDirectories)
            {
                AddDirectory(
                    searchDirectories,
                    directory.Path,
                    directory.IsPrivate);
            }
        }

        return new CompilerInstallation(
            preferred.Candidate,
            preferred.Probe!,
            preferred.Probe!.Family,
            aliases.ToArray(),
            searchDirectories.ToArray(),
            inspections.SelectMany(inspection => inspection.Candidate.Sources).Distinct().Order().ToArray(),
            preferred.Metadata!);
    }

    private static int DriverRank(string path) => CompilerName.Parse(path)?.DriverRank ?? 0;

    private static int InvocationRank(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Length == 0
            || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            ? 1
            : extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                ? 2
                : extension.Equals(".py", StringComparison.OrdinalIgnoreCase)
                    ? 3
                    : 4;
    }

    private static void AddPath(ICollection<string> paths, string path)
    {
        if (!paths.Contains(path, SearchPaths.Comparer))
        {
            paths.Add(path);
        }
    }

    private static void AddDirectory(
        IList<CompilerSearchDirectory> directories,
        string path,
        bool isPrivate)
    {
        int index = -1;
        for (int candidateIndex = 0;
            candidateIndex < directories.Count;
            candidateIndex++)
        {
            if (SearchPaths.Comparer.Equals(
                directories[candidateIndex].Path,
                path))
            {
                index = candidateIndex;
                break;
            }
        }
        if (index < 0)
        {
            directories.Add(new CompilerSearchDirectory(path, isPrivate));
        }
        else if (!isPrivate && directories[index].IsPrivate)
        {
            directories[index] =
                new CompilerSearchDirectory(path, IsPrivate: false);
        }
    }

    private static async Task<CompilerMetadata> InspectMetadataAsync(
        CompilerInvocationCandidate candidate, CompilerProbe probe,
        DiscoveryContext context, CancellationToken cancellationToken)
    {
        string environment = probe.IsApple
            ? AppleLocator.FindDeveloper(probe.ResolvedPath) ?? candidate.EnvironmentPath
            : candidate.EnvironmentPath;
        Version? productVersion = candidate.ProductVersion;
        string? productIdentity = null;
        if (probe.IsApple && productVersion is null && candidate.ProductChannel is null && Directory.Exists(Path.Combine(environment, "Platforms")))
        {
            Incant.Base.ProcessResult? product = await context.ProbeAsync("/usr/bin/xcodebuild", ["-version"],
                cancellationToken, AppleLocator.Environment(environment)).ConfigureAwait(false);
            productIdentity = product?.StandardOutput;
            productVersion = SearchPaths.Version(productIdentity);
        }

        Channel channel = candidate.ProductChannel
            ?? SearchPaths.Channel(string.Join(' ', probe.IdentityText, productIdentity, probe.IsApple ? environment : null));
        return new CompilerMetadata(environment, productVersion, channel);
    }

    private sealed record CompilerMetadata(string EnvironmentPath, Version? ProductVersion, Channel Channel);

    private sealed record CompilerInspection(
        CompilerInvocationCandidate Candidate,
        CompilerProbe? Probe,
        CompilerInspectionFailure? Failure,
        CompilerMetadata? Metadata = null);
}
