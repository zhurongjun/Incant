using Incant.Base;
using Incant.CX;

namespace Incant.CX.FindTools;

/// <summary>Finds standalone GNU and LLVM environments from explicit paths, active variables and standard search paths.</summary>
public sealed class CompilerProvider : IDiscoveryProvider
{
    /// <inheritdoc />
    public string Name => "GCC/LLVM";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds { get; } =
        Array.AsReadOnly(new[] { Kind.Gnu, Kind.Llvm });

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(
        ToolSetQuery query,
        DiscoveryContext context,
        CancellationToken cancellationToken)
    {
        CompilerDiscoveryResult discovery =
            await CompilerInstallation.DiscoverAsync(
                query.RootPath,
                context,
                cancellationToken).ConfigureAwait(false);
        CompilerInstallation[] supported = discovery.Installations
            .Where(installation =>
                installation.Family is CompilerFamily.Gnu
                    or CompilerFamily.Llvm)
            .ToArray();
        ToolSet[] toolSets = supported
            .Where(installation => Matches(
                installation.Family,
                query.Kind))
            .Select(installation =>
                (ToolSet)new CompilerToolSet(installation, context))
            .ToArray();
        var diagnostics = new List<Diagnostic>(discovery.Failures
            .Select(failure => new Diagnostic(
                failure.Severity,
                failure.Severity == DiagnosticSeverity.Info ? "excluded-candidate" : "invalid-candidate",
                Name,
                failure.Message,
                failure.Path)));
        if (query.RootPath is not null
            && query.Kind is Kind.Gnu or Kind.Llvm
            && discovery.Installations.Count > 0
            && toolSets.Length == 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Info,
                "different-toolset-family",
                Name,
                "The recognized compiler belongs to a different toolset family.",
                query.RootPath));
        }

        var result = new DiscoveryResult(toolSets, diagnostics);
        return query.RootPath is not null
            && discovery.Installations.Count > 0
                ? result.WithRecognizedInputs([query.RootPath])
                : result;
    }

    private static bool Matches(
        CompilerFamily family,
        Kind? kind) => kind switch
        {
            null => true,
            Kind.Gnu => family == CompilerFamily.Gnu,
            Kind.Llvm => family == CompilerFamily.Llvm,
            _ => false,
        };
}
