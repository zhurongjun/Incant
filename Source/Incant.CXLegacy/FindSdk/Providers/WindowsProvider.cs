using Incant.CXLegacy;

namespace Incant.CXLegacy.FindSdk;

/// <summary>Discovers Windows Kit and MSVC development files without requiring complete installations.</summary>
public sealed class WindowsProvider : IDiscoveryProvider
{
    /// <inheritdoc />
    public string Name => "Windows/MSVC";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds { get; } = Array.AsReadOnly(new[] { Kind.Windows, Kind.Msvc });

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(SdkQuery query, DiscoveryContext context, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DiscoveryResult();
        }

        var sdks = new List<Sdk>();
        var diagnostics = new List<Diagnostic>();
        var recognizedInputs = new List<string>();
        if (query.Kind is null or Kind.Windows)
        {
            foreach (Candidate candidate in WindowsLocator.WindowsKits(query.RootPath, context))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    sdks.AddRange(InspectKit(candidate));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(Resources.Missing(Name, exception.Message, candidate.Path));
                }
            }
        }

        if (query.Kind is null or Kind.Msvc)
        {
            string? compilerRoot = null;
            if (query.CompilerPath is string compilerPath)
            {
                compilerRoot = WindowsLocator.MsvcRootForCompiler(compilerPath);
                if (compilerRoot is not null)
                {
                    recognizedInputs.Add(compilerPath);
                }
            }

            IReadOnlyList<Candidate> candidates = await WindowsLocator.VisualStudiosAsync(
                query.RootPath ?? query.CompilerPath, context, cancellationToken).ConfigureAwait(false);
            foreach (Candidate candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string[] roots = WindowsLocator.MsvcRoots(candidate.Path)
                        .Select(SearchPaths.Normalize)
                        .Distinct(SearchPaths.Comparer)
                        .ToArray();
                    if (query.RootPath is not null && roots.Length > 0)
                    {
                        recognizedInputs.Add(query.RootPath);
                    }

                    foreach (string root in roots)
                    {
                        if (query.CompilerPath is not null
                            && (compilerRoot is null
                                || !SearchPaths.Comparer.Equals(root, compilerRoot)))
                        {
                            continue;
                        }

                        sdks.Add(InspectMsvc(root, candidate, query.CompilerPath));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(Resources.Missing(Name, exception.Message, candidate.Path));
                }
            }
        }

        return new DiscoveryResult(sdks, diagnostics)
            .WithRecognizedInputs(recognizedInputs);
    }

    private Sdk InspectMsvc(string root, Candidate candidate, string? compilerPath)
    {
        var layouts = new List<TargetLayout>();
        var common = new ResourceCollector(root);
        Resources.Headers(common, Path.Combine(root, "include"));
        Resources.Headers(common, Path.Combine(root, "atlmfc", "include"));
        var diagnostics = new List<Diagnostic>();
        RequireFile(diagnostics, Path.Combine(root, "include", "vcruntime.h"), "MSVC runtime headers");
        foreach ((string name, TargetArchitecture architecture) in WindowsLocator.Architectures)
        {
            string library = Path.Combine(root, "lib", name);
            string atlLibrary = Path.Combine(root, "atlmfc", "lib", name);
            if (!Directory.Exists(library) && !Directory.Exists(atlLibrary))
            {
                continue;
            }

            var resources = new ResourceCollector(root);
            resources.AddRange(common.Build());
            var targetDiagnostics = new List<Diagnostic>();
            Resources.Libraries(resources, library);
            Resources.Libraries(resources, atlLibrary);
            if (!File.Exists(Path.Combine(library, "libcmt.lib")) && !File.Exists(Path.Combine(library, "msvcrt.lib")))
            {
                targetDiagnostics.Add(Resources.Missing(Name, "The native MSVC C runtime is missing.", library));
            }

            if (!File.Exists(Path.Combine(library, "libvcruntime.lib")) && !File.Exists(Path.Combine(library, "vcruntime.lib")))
            {
                targetDiagnostics.Add(Resources.Missing(Name, "The MSVC runtime support library is missing.", library));
            }

            layouts.Add(new TargetLayout(TargetPlatform.Windows, architecture, resources.Build(), diagnostics: targetDiagnostics));
        }

        if (layouts.Count == 0)
        {
            layouts.Add(new TargetLayout(TargetPlatform.Windows, TargetArchitecture.Unknown, common.Build(),
                diagnostics: [Resources.Missing(Name, "No MSVC target library layout was found.", root)]));
        }

        return new Sdk(Kind.Msvc, root, layouts, SearchPaths.Version(Path.GetFileName(root)),
            WindowsLocator.MsvcEnvironment(root), candidate.ProductVersion, compilerPath,
            candidate.Channel ?? SearchPaths.Channel(candidate.Path), candidate.Sources, diagnostics);
    }

    private IEnumerable<Sdk> InspectKit(Candidate candidate)
    {
        string root = candidate.Path;
        string? requestedVersion = null;
        if (string.Equals(Path.GetFileName(root), "Include", StringComparison.OrdinalIgnoreCase))
        {
            root = Path.GetDirectoryName(root)!;
        }
        else if (string.Equals(Path.GetFileName(Path.GetDirectoryName(root)), "Include", StringComparison.OrdinalIgnoreCase))
        {
            requestedVersion = Path.GetFileName(root);
            root = Path.GetDirectoryName(Path.GetDirectoryName(root))!;
        }

        string[] versions = SearchPaths.Directories(Path.Combine(root, "Include"))
            .Concat(SearchPaths.Directories(Path.Combine(root, "Lib"))).Select(Path.GetFileName).OfType<string>()
            .Where(name => Version.TryParse(name, out _) && (requestedVersion is null || name == requestedVersion))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        foreach (string versionName in versions)
        {
            string include = Path.Combine(root, "Include", versionName);
            var common = new ResourceCollector(root);
            foreach (string part in new[] { "ucrt", "shared", "um", "winrt", "cppwinrt" })
            {
                Resources.Headers(common, Path.Combine(include, part));
            }

            var diagnostics = new List<Diagnostic>();
            RequireFile(diagnostics, Path.Combine(include, "um", "windows.h"), "Windows UM headers");
            RequireFile(diagnostics, Path.Combine(include, "ucrt", "corecrt.h"), "Windows UCRT headers");
            var layouts = new List<TargetLayout>();
            foreach ((string name, TargetArchitecture architecture) in WindowsLocator.Architectures)
            {
                string um = Path.Combine(root, "Lib", versionName, "um", name);
                string ucrt = Path.Combine(root, "Lib", versionName, "ucrt", name);
                if (!Directory.Exists(um) && !Directory.Exists(ucrt))
                {
                    continue;
                }

                var resources = new ResourceCollector(root);
                resources.AddRange(common.Build());
                var targetDiagnostics = new List<Diagnostic>();
                Resources.Libraries(resources, ucrt);
                Resources.Libraries(resources, um);
                RequireFile(targetDiagnostics, Path.Combine(um, "kernel32.lib"), "Windows UM libraries");
                RequireFile(targetDiagnostics, Path.Combine(ucrt, "ucrt.lib"), "Windows UCRT libraries");
                layouts.Add(new TargetLayout(TargetPlatform.Windows, architecture, resources.Build(), diagnostics: targetDiagnostics));
            }

            if (layouts.Count == 0)
            {
                layouts.Add(new TargetLayout(TargetPlatform.Windows, TargetArchitecture.Unknown, common.Build(),
                    diagnostics: [Resources.Missing(Name, "No target library layout was found for this Windows Kit version.", include)]));
            }

            yield return new Sdk(Kind.Windows, root, layouts, Version.Parse(versionName),
                sources: candidate.Sources, diagnostics: diagnostics);
        }
    }

    private void RequireFile(List<Diagnostic> diagnostics, string path, string description)
    {
        if (!File.Exists(path))
        {
            diagnostics.Add(Resources.Missing(Name, description + " are missing.", path));
        }
    }
}
