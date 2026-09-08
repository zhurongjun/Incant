using Incant.Base;
using Incant.CX;

namespace Incant.CX.FindTools;

/// <summary>Finds installed Xcode toolchains and Command Line Tools without requiring a platform SDK.</summary>
public sealed class AppleProvider : IDiscoveryProvider
{
    /// <inheritdoc />
    public string Name => "Apple";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds { get; } = Array.AsReadOnly(new[] { Kind.Xcode });

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(ToolSetQuery query, DiscoveryContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<Candidate> candidates = await AppleLocator.EnvironmentsAsync(query.RootPath, context, cancellationToken).ConfigureAwait(false);
        DiscoveryResult[] results = await Task.WhenAll(candidates.Select(candidate => Task.Run(
            () => InspectAsync(candidate, context, cancellationToken), cancellationToken))).ConfigureAwait(false);
        return new DiscoveryResult(results.SelectMany(result => result.ToolSets), results.SelectMany(result => result.Diagnostics));
    }

    private async Task<DiscoveryResult> InspectAsync(Candidate candidate, DiscoveryContext context, CancellationToken cancellationToken)
    {
        try
        {
            string developer = candidate.Path;
            string[] roots = SearchPaths.Directories(Path.Combine(developer, "Toolchains")).ToArray();
            if (roots.Length == 0)
            {
                roots = [developer];
            }

            string? developerParent = Path.GetDirectoryName(developer);
            if (developer.EndsWith(".xctoolchain", StringComparison.Ordinal) && developerParent is not null)
            {
                developer = Path.GetDirectoryName(developerParent)!;
            }

            IReadOnlyDictionary<string, string?> environment = AppleLocator.Environment(developer);
            ProcessResult? product = Directory.Exists(Path.Combine(developer, "Platforms"))
                ? await context.ProbeAsync("/usr/bin/xcodebuild", ["-version"], cancellationToken, environment).ConfigureAwait(false)
                : null;
            var toolSets = new List<ToolSet>();
            foreach (string root in roots)
            {
                string? compiler = SearchPaths.Executable(Path.Combine(root, "usr", "bin"), "clang");
                if (compiler is null)
                {
                    continue;
                }

                ProcessResult? version = await context.ProbeAsync(compiler, ["--version"], cancellationToken, environment).ConfigureAwait(false);
                if (version is null)
                {
                    continue;
                }

                ProcessResult? target = await context.ProbeAsync(compiler, ["-dumpmachine"], cancellationToken, environment).ConfigureAwait(false);
                toolSets.Add(new AppleToolSet(root, developer, compiler,
                    SearchPaths.CompilerVersion(version.StandardOutput), SearchPaths.Version(product?.StandardOutput),
                    target?.StandardOutput.Trim(), candidate.Sources, context));
            }

            return new DiscoveryResult(toolSets);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DiscoveryResult(diagnostics: [new Diagnostic(DiagnosticSeverity.Warning,
                "invalid-candidate", Name, exception.Message, candidate.Path)]);
        }
    }

    private sealed class AppleToolSet : ToolSet
    {
        private readonly DiscoveryContext _context;

        internal AppleToolSet(string root, string developer, string compiler, Version? version,
            Version? product, string? triple, IEnumerable<Source> sources, DiscoveryContext context)
            : base(Kind.Xcode, root, developer, version, product, version, compiler, triple,
                SearchPaths.Channel(developer), sources)
        {
            _context = context;
        }

        protected override async Task<Tool?> FindToolCoreAsync(
            string name,
            ToolQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? path = SearchPaths.Executable(
                Path.Combine(RootPath, "usr", "bin"),
                name);
            if (path is null && name is not "clang" and not "clang++")
            {
                ProcessResult? result = await _context.ProbeAsync(
                    "/usr/bin/xcrun",
                    [
                        "--no-cache",
                        "--sdk",
                        AppleLocator.SdkName(query.TargetPlatform),
                        "--find",
                        name,
                    ],
                    cancellationToken,
                    AppleLocator.Environment(EnvironmentPath)).ConfigureAwait(false);
                string? reported = result?.StandardOutput.Trim();
                if (reported is not null
                    && Path.IsPathFullyQualified(reported)
                    && File.Exists(reported))
                {
                    string identity = SearchPaths.Normalize(reported);
                    if (SearchPaths.Contains(EnvironmentPath, identity)
                        && (!SearchPaths.Contains(
                            Path.Combine(EnvironmentPath, "Toolchains"),
                            identity)
                            || SearchPaths.Contains(RootPath, identity)))
                    {
                        path = reported;
                    }
                }
            }

            if (path is null)
            {
                return null;
            }

            TargetArchitecture host =
                await HostExecutableInspector.SelectAsync(
                    path,
                    query.HostArchitecture,
                    _context,
                    allowsLaunchers: true,
                    cancellationToken).ConfigureAwait(false);
            if (query.HostArchitecture is not null
                && host == TargetArchitecture.Unknown)
            {
                return null;
            }

            return new Tool(name, path, host);
        }
    }
}
