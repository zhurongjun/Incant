using System.Diagnostics;
using Incant.Core.Cpp;

namespace Incant.Core.Cpp.FindTools;

/// <summary>Finds concrete MSVC toolset versions without requiring optional sibling tools or a Windows SDK.</summary>
public sealed class VisualStudioProvider : IDiscoveryProvider
{
    /// <inheritdoc />
    public string Name => "Visual Studio";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds { get; } = Array.AsReadOnly(new[] { Kind.VisualStudio });

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(ToolSetQuery query, DiscoveryContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<Candidate> candidates = await WindowsLocator.VisualStudiosAsync(query.RootPath, context, cancellationToken).ConfigureAwait(false);
        var results = new List<ToolSet>();
        var diagnostics = new List<Diagnostic>();
        foreach (Candidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (string root in WindowsLocator.MsvcRoots(candidate.Path))
                {
                    results.Add(new MsvcToolSet(root, candidate, context));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "invalid-candidate", Name, exception.Message, candidate.Path));
            }
        }

        return new DiscoveryResult(results, diagnostics);
    }

    private sealed class MsvcToolSet : ToolSet
    {
        private readonly DiscoveryContext _context;

        internal MsvcToolSet(string root, Candidate candidate, DiscoveryContext context)
            : base(Kind.VisualStudio, root, WindowsLocator.MsvcEnvironment(root),
                SearchPaths.Version(Path.GetFileName(root)), candidate.ProductVersion,
                CompilerVersionAt(root, context), PrimaryCompiler(root, context), channel: candidate.Channel ?? SearchPaths.Channel(candidate.Path),
                sources: candidate.Sources)
        {
            _context = context;
        }

        protected override Task<Tool?> FindToolCoreAsync(string name, ToolQuery query, CancellationToken cancellationToken) =>
            Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                TargetArchitecture target = query.TargetArchitecture ?? _context.HostArchitecture;
                foreach ((string hostName, _) in WindowsLocator.RunnableHosts(_context))
                {
                    string? targetName = WindowsLocator.Architectures.FirstOrDefault(item => item.Architecture == target).Name;
                    if (targetName is null)
                    {
                        return null;
                    }

                    string? path = SearchPaths.Executable(Path.Combine(RootPath, "bin", "Host" + hostName, targetName), name);
                    if (path is not null)
                    {
                        TargetArchitecture host = ExecutableArchitecture.Select(ExecutableArchitecture.Read(path), query.HostArchitecture);
                        if (query.HostArchitecture is null || host != TargetArchitecture.Unknown)
                        {
                            return new Tool(name, path, host, target);
                        }
                    }
                }

                return null;
            }, cancellationToken);

        private static string? PrimaryCompiler(string root, DiscoveryContext context) =>
            WindowsLocator.RunnableHosts(context).SelectMany(host => WindowsLocator.Architectures
                .Select(target => SearchPaths.Executable(Path.Combine(root, "bin", "Host" + host.Name, target.Name), "cl")))
                .FirstOrDefault(path => path is not null);

        private static Version? CompilerVersionAt(string root, DiscoveryContext context)
        {
            string? path = PrimaryCompiler(root, context);
            return path is null ? null : SearchPaths.Version(FileVersionInfo.GetVersionInfo(path).FileVersion);
        }
    }
}
