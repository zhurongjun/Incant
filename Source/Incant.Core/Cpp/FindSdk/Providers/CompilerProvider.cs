using Incant.Core.Cpp;

namespace Incant.Core.Cpp.FindSdk;

/// <summary>Reports development files from fresh, target-aware GCC and Clang driver queries.</summary>
public sealed class CompilerProvider : IDiscoveryProvider
{
    /// <inheritdoc />
    public string Name => "Compiler development files";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds { get; } = Array.AsReadOnly(new[] { Kind.Gnu, Kind.Llvm, Kind.AppleClang });

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(SdkQuery query, DiscoveryContext context, CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        var diagnostics = new List<Diagnostic>();
        if (query.CompilerPath is not null)
        {
            candidates.Add(new Candidate(query.CompilerPath, Source.Explicit));
        }
        else
        {
            var searches = new List<Task<IReadOnlyList<Candidate>>>();
            if (query.Kind is null or Kind.Gnu)
            {
                searches.Add(CompilerLocator.FindAsync(true, query.RootPath, context, cancellationToken));
            }

            if (query.Kind is null or Kind.Llvm)
            {
                searches.Add(CompilerLocator.FindAsync(false, query.RootPath, context, cancellationToken));
            }

            foreach (IReadOnlyList<Candidate> found in await Task.WhenAll(searches).ConfigureAwait(false))
            {
                candidates.AddRange(found);
            }

            if (query.Kind is null or Kind.Llvm)
            {
                BundleDiscoveryResult[] bundles = await Task.WhenAll(Enum.GetValues<BundleKind>()
                    .Select(kind => BundleLocator.FindAsync(kind, query.RootPath, context, cancellationToken))).ConfigureAwait(false);
                foreach (BundleDiscoveryResult bundle in bundles)
                {
                    diagnostics.AddRange(bundle.Diagnostics);
                    foreach (BundleInstallation installation in bundle.Installations)
                    {
                        string? compiler = installation.Kind == BundleKind.Emscripten
                            ? SearchPaths.Executable(Path.GetFullPath(Path.Combine(installation.Root, "..", "bin")), "clang")
                            : installation.Compiler;
                        if (compiler is not null)
                        {
                            candidates.Add(new Candidate(compiler, installation.Candidate.Sources.Min(), installation.Version, installation.Channel));
                        }
                    }
                }
            }

            if (query.Kind is null or Kind.AppleClang)
            {
                foreach (Candidate developer in await AppleLocator.EnvironmentsAsync(query.RootPath, context, cancellationToken).ConfigureAwait(false))
                {
                    foreach (string root in SearchPaths.Directories(Path.Combine(developer.Path, "Toolchains")).Append(developer.Path))
                    {
                        string? compiler = SearchPaths.Executable(Path.Combine(root, "usr", "bin"), "clang");
                        if (compiler is not null)
                        {
                            candidates.Add(new Candidate(compiler, developer.Sources.Min()));
                        }
                    }
                }
            }
        }

        DiscoveryResult[] results = await Task.WhenAll(Candidate.Merge(candidates).Select(candidate =>
            Task.Run(() => InspectAsync(candidate, query, context, cancellationToken), cancellationToken))).ConfigureAwait(false);
        return new DiscoveryResult(results.SelectMany(result => result.Sdks), diagnostics.Concat(results.SelectMany(result => result.Diagnostics)))
            .WithRecognizedInputs(results.SelectMany(result => result.RecognizedInputs));
    }

    private async Task<DiscoveryResult> InspectAsync(Candidate candidate, SdkQuery query, DiscoveryContext context, CancellationToken cancellationToken)
    {
        try
        {
            CompilerProbe? compiler = await CompilerProbe.OpenAsync(candidate.Path, context, cancellationToken).ConfigureAwait(false);
            if (compiler is null)
            {
                return new DiscoveryResult(diagnostics: [Resources.Missing(Name, "The compiler identity query failed.", candidate.Path)]);
            }

            Kind kind = compiler.IsApple ? Kind.AppleClang : compiler.IsClang ? Kind.Llvm : Kind.Gnu;
            if (query.Kind is not null && kind != query.Kind)
            {
                return new DiscoveryResult(diagnostics: [Resources.Missing(Name, "The compiler belongs to a different SDK family.", candidate.Path)])
                    .WithRecognizedInputs([candidate.Path]);
            }

            CompilerTargets targets = await compiler.FindTargetsAsync(query, cancellationToken).ConfigureAwait(false);
            Task<TargetLayout>[] tasks = targets.Targets.Select(target => CollectAsync(target, cancellationToken)).ToArray();
            TargetLayout[] layouts = await Task.WhenAll(tasks).ConfigureAwait(false);
            string root = targets.Targets.Select(target => target.ResourceDirectory).FirstOrDefault(path => path is not null)
                ?? compiler.Prefix;
            return new DiscoveryResult([new Sdk(kind, root, layouts, compiler.Version, compiler.Prefix,
                candidate.ProductVersion, candidate.Path, candidate.Channel ?? SearchPaths.Channel(compiler.IdentityText),
                candidate.Sources, targets.Diagnostics)]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DiscoveryResult(diagnostics: [Resources.Missing(Name, exception.Message, candidate.Path)]);
        }
    }

    private async Task<TargetLayout> CollectAsync(CompilerTarget target, CancellationToken cancellationToken)
    {
        var resources = new ResourceCollector();
        var diagnostics = new List<Diagnostic>(target.Diagnostics);
        try
        {
            if (target.ResourceDirectory is string resourceDirectory)
            {
                resources.Own(resourceDirectory);
                resources.Add(ResourcePurpose.Builtin, resourceDirectory);
            }
            else
            {
                diagnostics.Add(Resources.Missing(Name, "The compiler resource directory is missing.", target.Compiler.Path));
            }

            string? libgcc = await target.FindFileAsync("libgcc.a", cancellationToken).ConfigureAwait(false);
            if (!target.Compiler.IsClang && libgcc is not null
                && ExecutableArchitecture.MatchesTarget(libgcc, target.Identity) is true)
            {
                resources.Own(Path.GetDirectoryName(libgcc)!);
            }

            string cppInclude = Path.Combine(target.Compiler.Prefix, "include", "c++", "v1");
            if (target.Compiler.IsClang && Directory.Exists(cppInclude))
            {
                resources.Own(cppInclude);
            }
            else if (!target.Compiler.IsClang)
            {
                foreach (CompilerInclude include in target.Includes)
                {
                    if (include.Path.Contains(Path.DirectorySeparatorChar + "c++" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        resources.Own(include.Path);
                    }
                }
            }

            bool hasForeignLibc = TargetResources.HasForeignLibc(target);
            foreach (CompilerInclude include in target.Includes)
            {
                if ((resources.Owns(include.Path) || !TargetResources.HasConflictingDirectory(include.Path, target.Identity))
                    && (!hasForeignLibc || resources.Owns(include.Path) || TargetResources.HasCompatibleDirectory(include.Path, target.Identity)))
                {
                    resources.Add(include.Purpose, include.Path);
                }
            }

            foreach (string directory in target.LibraryDirectories)
            {
                if ((resources.Owns(directory) || !TargetResources.HasConflictingDirectory(directory, target.Identity))
                    && (!hasForeignLibc || resources.Owns(directory) || TargetResources.HasCompatibleDirectory(directory, target.Identity)))
                {
                    resources.Add(ResourcePurpose.LibraryDirectory, directory);
                }
            }

            string[] names = ["libgcc.a", "libgcc_s.so", "libstdc++.a", "libstdc++.so", "libc++.a", "libc++.so",
                "libc++.dylib", "libc++abi.a", "libunwind.a", "crt1.o", "Scrt1.o", "rcrt1.o", "crti.o", "crtn.o",
                "crtbegin.o", "crtbeginS.o", "crtbeginT.o", "crtend.o", "crtendS.o", "clang_rt.crtbegin.o", "clang_rt.crtend.o"];
            string?[] paths = await Task.WhenAll(names.Select(name => target.FindFileAsync(name, cancellationToken))).ConfigureAwait(false);
            bool hasUnconfirmedFiles = false;
            foreach (string path in paths.OfType<string>())
            {
                if (TargetResources.IsCompatibleFile(path, target, hasForeignLibc, resources.Owns(path)))
                {
                    resources.Add(TargetResources.IsStartup(path) ? ResourcePurpose.Startup : ResourcePurpose.Library, path);
                }
                else
                {
                    hasUnconfirmedFiles = true;
                }
            }

            if (target.Compiler.IsClang)
            {
                foreach (string directory in new[] { Path.Combine(target.Compiler.Prefix, "lib", "c++"),
                    Path.Combine(target.Compiler.Prefix, "lib", target.Identity.Triple) })
                {
                    resources.Own(directory);
                    foreach (string file in SearchPaths.Files(directory).Where(TargetResources.IsLibrary))
                    {
                        if (TargetResources.IsCompatibleFile(file, target, hasForeignLibc))
                        {
                            resources.Add(ResourcePurpose.RuntimeDirectory, directory);
                            resources.Add(ResourcePurpose.Library, file);
                        }
                    }
                }

                bool hasRuntime = false;
                if (target.ResourceDirectory is string root)
                {
                    foreach (string directory in SearchPaths.Directories(Path.Combine(root, "lib")))
                    {
                        foreach (string file in SearchPaths.Files(directory).Where(file => TargetResources.IsLibrary(file) || TargetResources.IsStartup(file)))
                        {
                            if (TargetResources.RuntimeMatches(directory, file, target.Identity)
                                && (!hasForeignLibc || TargetResources.HasCompatibleDirectory(directory, target.Identity)))
                            {
                                resources.Add(ResourcePurpose.RuntimeDirectory, directory);
                                resources.Add(TargetResources.IsStartup(file) ? ResourcePurpose.Startup : ResourcePurpose.Library, file);
                                hasRuntime = true;
                            }
                        }
                    }
                }

                if (!hasRuntime)
                {
                    diagnostics.Add(Resources.Missing(Name, "No installed compiler runtime was confirmed for this target.", target.Compiler.Path));
                }
            }

            if (hasUnconfirmedFiles)
            {
                diagnostics.Add(Resources.Missing(Name, "Some driver-reported files could not be confirmed for this target ABI and were excluded.", target.Compiler.Path));
            }

            if (hasForeignLibc)
            {
                diagnostics.Add(Resources.Missing(Name, "Unqualified host paths were excluded because their libc does not match the target.", target.Compiler.Path));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Resources.Missing(Name, exception.Message, target.Compiler.Path));
        }

        return new TargetLayout(target.Identity.Platform, target.Identity.Architecture, resources.Build(),
            target.Identity.Triple, target.Sysroot, multilib: target.Multilib, diagnostics: diagnostics);
    }
}
