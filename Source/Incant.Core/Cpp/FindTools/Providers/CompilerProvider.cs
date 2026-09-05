using Incant.Base;
using Incant.Core.Cpp;

namespace Incant.Core.Cpp.FindTools;

/// <summary>Finds standalone GNU and LLVM environments from explicit paths, active variables and standard search paths.</summary>
public sealed class CompilerProvider : IDiscoveryProvider
{
    /// <inheritdoc />
    public string Name => "GCC/LLVM";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds { get; } = Array.AsReadOnly(new[] { Kind.Gnu, Kind.Llvm });

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(ToolSetQuery query, DiscoveryContext context, CancellationToken cancellationToken)
    {
        Kind[] kinds = Kinds.Where(kind => query.Kind is null || query.Kind == kind).ToArray();
        var tasks = new List<Task<DiscoveryResult>>();
        foreach (Kind kind in kinds)
        {
            IReadOnlyList<Candidate> candidates = await CompilerLocator.FindAsync(kind == Kind.Gnu, query.RootPath,
                context, cancellationToken).ConfigureAwait(false);
            tasks.AddRange(candidates.Select(candidate => Task.Run(
                () => InspectAsync(kind, candidate, context, cancellationToken), cancellationToken)));
        }

        DiscoveryResult[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new DiscoveryResult(results.SelectMany(result => result.ToolSets), results.SelectMany(result => result.Diagnostics));
    }

    private async Task<DiscoveryResult> InspectAsync(Kind kind, Candidate candidate, DiscoveryContext context, CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult? identity = await context.ProbeAsync(candidate.Path, ["--version"], cancellationToken).ConfigureAwait(false);
            string text = identity?.StandardOutput + identity?.StandardError;
            bool isClang = text.Contains("clang", StringComparison.OrdinalIgnoreCase);
            if (identity is null || isClang != (kind == Kind.Llvm)
                || text.Contains("Apple clang", StringComparison.OrdinalIgnoreCase))
            {
                return Invalid(candidate);
            }

            Version? version = SearchPaths.CompilerVersion(text);
            if (kind == Kind.Gnu)
            {
                ProcessResult? result = await context.ProbeAsync(candidate.Path, ["-dumpfullversion", "-dumpversion"], cancellationToken).ConfigureAwait(false);
                version = SearchPaths.Version(result?.StandardOutput) ?? version;
            }

            ProcessResult? target = await context.ProbeAsync(candidate.Path, ["-dumpmachine"], cancellationToken).ConfigureAwait(false);
            string? triple = target?.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(triple))
            {
                triple = text.Split('\n').FirstOrDefault(line => line.StartsWith("Target:", StringComparison.Ordinal))?["Target:".Length..].Trim();
            }

            if (triple?.Contains("mingw", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new DiscoveryResult();
            }

            string bin = Path.GetDirectoryName(candidate.Path)!;
            var toolSet = new DirectoryToolSet(kind, bin, Path.GetDirectoryName(bin)!, bin,
                version, version, version, candidate.Path, triple, SearchPaths.Channel(text),
                candidate.Sources);
            return new DiscoveryResult([toolSet]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid(candidate, exception.Message);
        }
    }

    private DiscoveryResult Invalid(Candidate candidate, string? message = null) => new(diagnostics:
        [new Diagnostic(DiagnosticSeverity.Warning, "invalid-candidate", Name,
            message ?? "The compiler identity could not be established.", candidate.Path)]);
}
