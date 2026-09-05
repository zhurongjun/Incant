using Incant.Base;
using Incant.Core.Cpp;
using Incant.Core.Cpp.FindSdk;
using Incant.Core.Cpp.FindTools;
using SdkFinder = Incant.Core.Cpp.FindSdk.Finder;
using SdkKind = Incant.Core.Cpp.FindSdk.Kind;
using ToolFinder = Incant.Core.Cpp.FindTools.Finder;
using ToolKind = Incant.Core.Cpp.FindTools.Kind;

internal static class AutoTestDiscovery
{
    internal static async Task<AutoTestRun> DiscoverAsync(
        AutoTestCommand command, string? explicitRoot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ToolKind? kind = command.Kind is null ? null : command.Kind == AutoTestKind.WindowsSdk
            ? ToolKind.VisualStudio : Enum.Parse<ToolKind>(command.Kind.ToString()!);
        ToolFinder finder = ToolFinder.CreateDefault();
        Incant.Core.Cpp.FindTools.DiscoveryResult result = await finder.FindToolSetsAsync(new ToolSetQuery
        {
            Kind = kind,
            RootPath = command.Kind == AutoTestKind.WindowsSdk ? null : explicitRoot,
            ProductVersion = command.Kind == AutoTestKind.WindowsSdk ? null : Major(command.ProductMajor),
            CompilerVersion = Major(command.CompilerMajor),
            IncludePreview = command.IncludePreview,
        }, cancellationToken).ConfigureAwait(false);
        var run = new AutoTestRun(explicitRoot is null ? "automatic" : "explicit", result.ToolSets, result.Diagnostics)
        {
            DiscoveredInstallationCount = result.ToolSets.Count,
        };
        if (command.Operation == AutoTestOperation.Discover)
        {
            Incant.Core.Cpp.FindSdk.DiscoveryResult sdkResult = await SdkFinder.CreateDefault().FindSdksAsync(
                new SdkQuery
                {
                    Kind = command.Kind switch
                    {
                        AutoTestKind.WindowsSdk => SdkKind.Windows,
                        AutoTestKind.VisualStudio => SdkKind.Msvc,
                        AutoTestKind.Gnu => SdkKind.Gnu,
                        AutoTestKind.Llvm => SdkKind.Llvm,
                        AutoTestKind.Xcode => SdkKind.Apple,
                        AutoTestKind.AndroidNdk => SdkKind.AndroidNdk,
                        AutoTestKind.Emscripten => SdkKind.Emscripten,
                        AutoTestKind.WasiSdk => SdkKind.WasiSdk,
                        _ => null,
                    },
                    RootPath = explicitRoot,
                    IncludePreview = command.IncludePreview,
                }, cancellationToken).ConfigureAwait(false);
            run.Sdks.AddRange(sdkResult.Sdks);
            run.Diagnostics.AddRange(sdkResult.Diagnostics);
            if (command.Kind == AutoTestKind.WindowsSdk)
            {
                run.DiscoveredInstallationCount = sdkResult.Sdks.Count;
            }
        }

        return run;
    }

    internal static async Task<SmokeConfiguration> ConfigureAsync(
        AutoTestCommand command, AutoTestRun run, string? explicitRoot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Operation == AutoTestOperation.VerifyClangCl && command.ClangClLinker is not (ClangClLinker.Msvc or ClangClLinker.Lld))
        {
            throw new ArgumentException("clang-cl verification requires a linker mode.", nameof(command));
        }

        run.DiscoveredInstallationCount = command.Kind == AutoTestKind.WindowsSdk ? 0 : run.ToolSets.Count;
        if (command.Kind != AutoTestKind.WindowsSdk)
        {
            VerifyMinimumCount(command, run);
        }

        foreach (ToolSet toolSet in run.ToolSets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SmokeConfiguration? configuration = await TryConfigureToolSetAsync(
                command, run, explicitRoot, toolSet, cancellationToken).ConfigureAwait(false);
            if (configuration is null)
            {
                continue;
            }

            VerifyMinimumCount(command, run);
            // Acceptance failures are not candidate absence and must never cause installation fallback.
            await VerifySelectionAsync(command, configuration, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Discovered {run.DiscoveredInstallationCount} matching {command.Kind} installation(s); "
                + $"selected one for {configuration.TargetPlatform}/{configuration.TargetArchitecture} C/C++ smoke verification.");
            return configuration;
        }

        VerifyMinimumCount(command, run);
        throw new AutoTestFailureException("No discovered combination has the C/C++ tools and development files required for smoke verification.");
    }

    private static async Task<SmokeConfiguration?> TryConfigureToolSetAsync(
        AutoTestCommand command, AutoTestRun run, string? explicitRoot, ToolSet toolSet, CancellationToken cancellationToken)
    {
        TargetPlatform platform = command.Target ?? DefaultPlatform(toolSet);
        TargetArchitecture architecture = command.Architecture ?? DefaultArchitecture(toolSet);
        var toolQuery = new ToolQuery { TargetArchitecture = architecture, TargetPlatform = platform };
        Tool? compiler = command.Operation == AutoTestOperation.VerifyClangCl
            ? await toolSet.FindToolAsync(ToolNames.ClangCl, toolQuery, cancellationToken).ConfigureAwait(false)
            : await FindComponentAsync(toolSet, ComponentKind.Compiler, architecture, platform, cancellationToken).ConfigureAwait(false);
        Tool? cppCompiler = command.Operation == AutoTestOperation.VerifyClangCl ? compiler
            : await FindComponentAsync(toolSet, ComponentKind.CppCompiler, architecture, platform, cancellationToken).ConfigureAwait(false);
        if ((compiler is null || cppCompiler is null) && command.Kind != AutoTestKind.WindowsSdk)
        {
            Skip(run, toolSet.RootPath, $"Missing {(compiler is null ? "C" : "C++")} compiler for {platform}/{architecture}.");
            return null;
        }

        SdkKind sdkKind = platform switch
        {
            TargetPlatform.Windows => SdkKind.Windows,
            TargetPlatform.Android => SdkKind.AndroidNdk,
            TargetPlatform.Emscripten => SdkKind.Emscripten,
            TargetPlatform.Wasi => SdkKind.WasiSdk,
            TargetPlatform.Linux => SdkKind.Linux,
            _ => SdkKind.Apple,
        };
        string? sdkRoot = sdkKind switch
        {
            SdkKind.Windows when command.Kind == AutoTestKind.WindowsSdk => explicitRoot,
            SdkKind.Apple when toolSet.Kind == ToolKind.Xcode => toolSet.EnvironmentPath,
            SdkKind.AndroidNdk or SdkKind.Emscripten or SdkKind.WasiSdk => toolSet.RootPath,
            _ => null,
        };
        IReadOnlyList<Sdk> sdks = await FindCandidateSdksAsync(run, new SdkQuery
        {
            Kind = sdkKind == SdkKind.Linux ? null : sdkKind,
            RootPath = sdkRoot,
            CompilerPath = sdkKind == SdkKind.Linux ? compiler?.Path : null,
            TargetPlatform = platform,
            TargetArchitecture = architecture,
            Version = Major(command.SdkMajor),
            IncludePreview = command.IncludePreview,
        }, allowMissing: command.Kind != AutoTestKind.WindowsSdk || explicitRoot is null, cancellationToken).ConfigureAwait(false);
        if (command.Kind == AutoTestKind.WindowsSdk)
        {
            run.DiscoveredInstallationCount = Math.Max(run.DiscoveredInstallationCount, sdks.Count);
        }

        if (compiler is null || cppCompiler is null)
        {
            Skip(run, toolSet.RootPath, $"Missing {(compiler is null ? "C" : "C++")} compiler for {platform}/{architecture}.");
            return null;
        }

        foreach (Sdk sdk in sdks)
        {
            foreach (TargetLayout layout in OrderedLayouts(sdk, platform, architecture))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? missing = MissingPlatformResources(toolSet, layout);
                if (missing is not null)
                {
                    Skip(run, sdk.RootPath, $"{Describe(layout)}: {missing}");
                    continue;
                }

                var candidate = new SmokeConfiguration(toolSet, sdk, layout, TargetTriple(layout), null, null, null)
                {
                    CCompiler = compiler,
                    CppCompiler = cppCompiler,
                };
                SmokeConfiguration? configuration = platform == TargetPlatform.Windows
                    ? await TryConfigureWindowsAsync(command, run, candidate, cancellationToken).ConfigureAwait(false)
                    : await TryCompleteConfigurationAsync(command, run, candidate, cancellationToken).ConfigureAwait(false);
                if (configuration is not null)
                {
                    return configuration;
                }
            }
        }

        Skip(run, toolSet.RootPath, $"No complete {sdkKind} SDK combination supports {platform}/{architecture}.");
        return null;
    }

    private static async Task<SmokeConfiguration?> TryConfigureWindowsAsync(
        AutoTestCommand command, AutoTestRun run, SmokeConfiguration candidate, CancellationToken cancellationToken)
    {
        IReadOnlyList<ToolSet> toolSets;
        if (candidate.ToolSet.Kind == ToolKind.VisualStudio)
        {
            toolSets = [candidate.ToolSet];
        }
        else
        {
            Incant.Core.Cpp.FindTools.DiscoveryResult result = await ToolFinder.CreateDefault().FindToolSetsAsync(new ToolSetQuery
            {
                Kind = ToolKind.VisualStudio,
                ProductVersion = Major(command.MsvcMajor),
                IncludePreview = command.IncludePreview,
            }, cancellationToken).ConfigureAwait(false);
            run.Diagnostics.AddRange(result.Diagnostics);
            VerifyDiscoveryDiagnostics(result.Diagnostics);
            toolSets = result.ToolSets;
        }

        if (toolSets.Count == 0)
        {
            Skip(run, candidate.ToolSet.RootPath, "Windows smoke linking has no matching MSVC development environment.");
            return null;
        }

        foreach (ToolSet toolSet in toolSets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<Sdk> sdks = await FindCandidateSdksAsync(run, new SdkQuery
            {
                Kind = SdkKind.Msvc,
                RootPath = toolSet.RootPath,
                TargetPlatform = candidate.TargetPlatform,
                TargetArchitecture = candidate.TargetArchitecture,
                IncludePreview = command.IncludePreview,
            }, allowMissing: true, cancellationToken).ConfigureAwait(false);
            foreach (Sdk sdk in sdks)
            {
                foreach (TargetLayout layout in OrderedLayouts(sdk, candidate.TargetPlatform, candidate.TargetArchitecture))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? missing = MissingMsvcResources(layout);
                    if (missing is not null)
                    {
                        Skip(run, sdk.RootPath, $"{Describe(layout)}: {missing}");
                        continue;
                    }

                    SmokeConfiguration? configuration = await TryCompleteConfigurationAsync(command, run, candidate with
                    {
                        MsvcToolSet = toolSet,
                        MsvcSdk = sdk,
                        MsvcLayout = layout,
                    }, cancellationToken).ConfigureAwait(false);
                    if (configuration is not null)
                    {
                        return configuration;
                    }
                }
            }

            Skip(run, toolSet.RootPath, $"No complete MSVC tool/SDK combination for {Describe(candidate.Layout)}.");
        }

        return null;
    }

    private static async Task<SmokeConfiguration?> TryCompleteConfigurationAsync(
        AutoTestCommand command, AutoTestRun run, SmokeConfiguration candidate, CancellationToken cancellationToken)
    {
        Tool? linker = await FindLinkerAsync(command, candidate, cancellationToken).ConfigureAwait(false);
        if (linker is null)
        {
            Skip(run, candidate.ToolSet.RootPath, $"{Describe(candidate.Layout)}: missing the linker used by "
                + $"{command.Operation}/{command.ClangClLinker}; MSVC={candidate.MsvcToolSet?.RootPath ?? "none"}.");
            return null;
        }

        candidate = candidate with { Linker = linker };
        if (candidate.ToolSet.Kind == ToolKind.VisualStudio)
        {
            candidate = candidate with { CompilerSdk = candidate.MsvcSdk, CompilerLayout = candidate.MsvcLayout };
            return await HasRequiredComponentsAsync(command, run, candidate, cancellationToken).ConfigureAwait(false) ? candidate : null;
        }

        Tool? developmentCompiler = candidate.ToolSet.Kind == ToolKind.Emscripten
            || command.Operation == AutoTestOperation.VerifyClangCl
                ? await candidate.ToolSet.FindToolAsync(ToolNames.Clang, new ToolQuery
                {
                    TargetPlatform = candidate.TargetPlatform,
                    TargetArchitecture = candidate.TargetArchitecture,
                }, cancellationToken).ConfigureAwait(false)
                : candidate.CCompiler;
        if (developmentCompiler is null)
        {
            Skip(run, candidate.ToolSet.RootPath, "Missing the Clang driver needed to query compiler development files.");
            return null;
        }

        string probeTriple = candidate.TargetPlatform == TargetPlatform.Android
            ? candidate.TargetTriple + AndroidApi(candidate.Layout)
            : candidate.TargetTriple;
        IReadOnlyList<Sdk> sdks = await FindCandidateSdksAsync(run, new SdkQuery
        {
            Kind = candidate.ToolSet.Kind == ToolKind.Gnu ? SdkKind.Gnu
                : candidate.ToolSet.Kind == ToolKind.Xcode ? SdkKind.AppleClang : SdkKind.Llvm,
            CompilerPath = developmentCompiler.Path,
            TargetPlatform = candidate.TargetPlatform,
            TargetArchitecture = candidate.TargetArchitecture,
            TargetTriple = candidate.ToolSet.Kind == ToolKind.Gnu ? candidate.Layout.TargetTriple : probeTriple,
            SysrootPath = candidate.Layout.SysrootPath,
            Multilib = candidate.ToolSet.Kind == ToolKind.Gnu ? candidate.Layout.Multilib : null,
            IncludePreview = command.IncludePreview,
        }, allowMissing: true, cancellationToken).ConfigureAwait(false);
        foreach (Sdk sdk in sdks)
        {
            foreach (TargetLayout layout in OrderedLayouts(sdk, candidate.TargetPlatform, candidate.TargetArchitecture))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSupportedVariant(candidate.ToolSet, layout))
                {
                    Skip(run, sdk.RootPath, $"{Describe(layout)}: this variant is not the requested smoke ABI.");
                    continue;
                }

                SmokeConfiguration configuration = candidate with
                {
                    CompilerSdk = sdk,
                    CompilerLayout = layout,
                    TargetTriple = candidate.ToolSet.Kind == ToolKind.Gnu ? layout.TargetTriple ?? candidate.TargetTriple : candidate.TargetTriple,
                };
                string? missing = MissingCompilerResources(configuration);
                if (missing is not null)
                {
                    Skip(run, sdk.RootPath, $"{Describe(layout)}: {missing}");
                    continue;
                }

                if (await HasRequiredComponentsAsync(command, run, configuration, cancellationToken).ConfigureAwait(false))
                {
                    return configuration;
                }
            }
        }

        Skip(run, candidate.ToolSet.RootPath, $"{Describe(candidate.Layout)}: no complete compiler SDK matches the platform layout.");
        return null;
    }

    private static async Task<bool> HasRequiredComponentsAsync(
        AutoTestCommand command, AutoTestRun run, SmokeConfiguration configuration, CancellationToken cancellationToken)
    {
        foreach (ComponentKind component in command.RequiredComponents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isPresent = component switch
            {
                ComponentKind.Compiler => File.Exists(configuration.CCompiler.Path),
                ComponentKind.CppCompiler => File.Exists(configuration.CppCompiler.Path),
                ComponentKind.Linker => configuration.Linker is not null && File.Exists(configuration.Linker.Path),
                ComponentKind.Sysroot => Directory.Exists(configuration.Layout.SysrootPath),
                ComponentKind.ResourceDirectory => configuration.CompilerLayout?.Resources
                    .Any(resource => resource.Purpose == ResourcePurpose.Builtin && Directory.Exists(resource.Path)) == true,
                _ => await FindComponentAsync(configuration.ToolSet, component, configuration.TargetArchitecture,
                    configuration.TargetPlatform, cancellationToken).ConfigureAwait(false) is not null,
            };
            if (!isPresent)
            {
                Skip(run, configuration.ToolSet.RootPath, $"{Describe(configuration.Layout)}: missing required {component}.");
                return false;
            }
        }

        return true;
    }

    private static async Task<IReadOnlyList<Sdk>> FindCandidateSdksAsync(
        AutoTestRun run, SdkQuery query, bool allowMissing, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Linux drivers can report either the native system SDK or an independent cross sysroot.
        SdkFinder finder = query.Kind is null && query.TargetPlatform == TargetPlatform.Linux
            ? new SdkFinder([new SystemProvider()]) : SdkFinder.CreateDefault();
        Incant.Core.Cpp.FindSdk.DiscoveryResult result;
        try
        {
            result = await finder.FindSdksAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (DiscoveryException exception) when (allowMissing && exception.InnerException is null
            && exception.Diagnostics.All(diagnostic => diagnostic.Code == "missing-resource"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Derived candidate paths may lack a component; invalid user inputs and provider bugs still escape.
            run.Diagnostics.AddRange(exception.Diagnostics);
            Skip(run, query.RootPath ?? query.CompilerPath, exception.Message);
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        run.Sdks.AddRange(result.Sdks);
        run.Diagnostics.AddRange(result.Diagnostics);
        VerifyDiscoveryDiagnostics(result.Diagnostics);
        return result.Sdks;
    }

    private static void VerifyDiscoveryDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            throw new DiscoveryException("Discovery failed while constructing a smoke candidate.", diagnostics);
        }
    }

    private static IEnumerable<TargetLayout> OrderedLayouts(Sdk sdk, TargetPlatform platform, TargetArchitecture architecture) =>
        sdk.Layouts.Where(layout => layout.Platform == platform && layout.Architecture == architecture)
            .OrderBy(layout => layout.Multilib == "." ? 0 : layout.Multilib is null ? 1 : 2);

    private static bool IsSupportedVariant(ToolSet toolSet, TargetLayout layout)
    {
        if (IsX32(layout.TargetTriple) && !IsX32(toolSet.DefaultTargetTriple))
        {
            return false;
        }

        return layout.Multilib is null or "."
            || toolSet.Kind == ToolKind.Gnu && layout.Multilib == (layout.Architecture == TargetArchitecture.X86 ? "32" : "64");
    }

    private static string? MissingPlatformResources(ToolSet toolSet, TargetLayout layout)
    {
        if (!IsSupportedVariant(toolSet, layout))
        {
            return "This library variant is not the requested smoke ABI.";
        }

        if (layout.Platform != TargetPlatform.Windows && !Directory.Exists(layout.SysrootPath))
        {
            return "Missing the target sysroot.";
        }

        if (!HasHeader(layout.Resources, ResourcePurpose.CInclude, "stdio.h")
            || layout.Platform == TargetPlatform.Windows && !HasHeader(layout.Resources, ResourcePurpose.CInclude, "corecrt.h"))
        {
            return "Missing the C standard headers required by the smoke source.";
        }

        string[] libraries = layout.Platform switch
        {
            TargetPlatform.Windows => ["libucrt.lib"],
            TargetPlatform.MacOS or TargetPlatform.IOS or TargetPlatform.IOSSimulator or TargetPlatform.TvOS
                or TargetPlatform.TvOSSimulator or TargetPlatform.WatchOS or TargetPlatform.WatchOSSimulator
                or TargetPlatform.VisionOS or TargetPlatform.VisionOSSimulator => ["libSystem.tbd", "libSystem.B.tbd", "libSystem.dylib", "libSystem.B.dylib"],
            _ => ["libc.so", "libc.a"],
        };
        if (!HasLibrary(layout.Resources, libraries, layout.Platform == TargetPlatform.Android ? AndroidApi(layout) : null)
            || layout.Platform == TargetPlatform.Windows && !HasLibrary(layout.Resources, ["kernel32.lib"]))
        {
            return "Missing the target C runtime or system import libraries used by smoke linking.";
        }

        if (layout.Platform == TargetPlatform.Android && AndroidApi(layout) == 0)
        {
            return "No installed Android API satisfies this architecture's smoke deployment minimum.";
        }

        return null;
    }

    private static string? MissingMsvcResources(TargetLayout layout)
    {
        if (!HasHeader(layout.Resources, ResourcePurpose.CppInclude, "iostream")
            || !HasHeader(layout.Resources, ResourcePurpose.CInclude, "vcruntime.h"))
        {
            return "Missing the MSVC C++ standard headers.";
        }

        foreach (string library in new[] { "libcmt.lib", "libcpmt.lib", "libvcruntime.lib" })
        {
            if (!HasLibrary(layout.Resources, [library]))
            {
                return $"Missing {library}, required by the default static MSVC runtime mode.";
            }
        }

        return null;
    }

    private static string? MissingCompilerResources(SmokeConfiguration configuration)
    {
        TargetLayout layout = configuration.CompilerLayout!;
        if (!layout.Resources.Any(resource => resource.Purpose == ResourcePurpose.CInclude)
            || !layout.Resources.Any(resource => resource.Purpose == ResourcePurpose.CppInclude))
        {
            return "Missing C or C++ compiler header search results.";
        }

        if (configuration.TargetPlatform == TargetPlatform.Windows)
        {
            return null;
        }

        Resource[] resources = layout.Resources.Concat(configuration.Layout.Resources).ToArray();
        if (!HasHeader(resources, ResourcePurpose.CppInclude, "iostream"))
        {
            return "Missing the C++ standard headers required by the smoke source.";
        }

        string[] libraries = configuration.ToolSet.Kind == ToolKind.Gnu
            ? ["libstdc++.so", "libstdc++.a", "libstdc++.dylib"]
            : ["libstdc++.so", "libstdc++.a", "libc++.so", "libc++.a", "libc++.tbd", "libc++.dylib", "libc++_shared.so", "libc++_static.a"];
        return HasLibrary(resources, libraries) ? null : "Missing the C++ standard library used by smoke linking.";
    }

    private static bool HasHeader(IEnumerable<Resource> resources, ResourcePurpose purpose, string name) =>
        resources.Any(resource => resource.Purpose == purpose && File.Exists(Path.Combine(resource.Path, name)));

    private static bool HasLibrary(IEnumerable<Resource> resources, string[] names, int? apiLevel = null) =>
        resources.Any(resource => resource.Purpose == ResourcePurpose.Library
            && (apiLevel is null || resource.ApiLevel is null || resource.ApiLevel == apiLevel)
            && names.Any(name => string.Equals(Path.GetFileName(resource.Path), name,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || name.EndsWith(".so", StringComparison.Ordinal)
                    && Path.GetFileName(resource.Path).StartsWith(name + ".", StringComparison.Ordinal))
            && File.Exists(resource.Path));

    private static int AndroidApi(TargetLayout layout) =>
        layout.ApiLevels.FirstOrDefault(level => level >= (layout.Architecture is TargetArchitecture.ARM64 or TargetArchitecture.X64 ? 21 : 16));

    private static bool IsX32(string? triple) => triple?.Contains("x32", StringComparison.Ordinal) == true;

    internal static string? GnuArchitectureArgument(TargetLayout layout) => layout.Multilib == "x32" || IsX32(layout.TargetTriple)
        ? "-mx32" : layout.Architecture switch
        {
            TargetArchitecture.X86 => "-m32",
            TargetArchitecture.X64 => "-m64",
            _ => null,
        };

    private static async Task<Tool?> FindLinkerAsync(
        AutoTestCommand command, SmokeConfiguration candidate, CancellationToken cancellationToken)
    {
        var query = new ToolQuery { TargetArchitecture = candidate.TargetArchitecture, TargetPlatform = candidate.TargetPlatform };
        if (candidate.TargetPlatform == TargetPlatform.Windows)
        {
            bool usesMsvc = candidate.ToolSet.Kind == ToolKind.VisualStudio
                || command.Operation == AutoTestOperation.VerifyClangCl && command.ClangClLinker == ClangClLinker.Msvc;
            ToolSet toolSet = usesMsvc ? candidate.MsvcToolSet! : candidate.ToolSet;
            return await toolSet.FindToolAsync(usesMsvc ? ToolNames.Link : ToolNames.LldLink, query, cancellationToken).ConfigureAwait(false);
        }

        string name = candidate.ToolSet.Kind switch
        {
            ToolKind.AndroidNdk => ToolNames.LdLld,
            ToolKind.Emscripten or ToolKind.WasiSdk => ToolNames.WasmLd,
            _ => ToolNames.Ld,
        };
        Tool? linker = await candidate.ToolSet.FindToolAsync(name, query, cancellationToken).ConfigureAwait(false);
        if (linker is not null || candidate.ToolSet.Kind is not (ToolKind.Gnu or ToolKind.Llvm or ToolKind.Xcode))
        {
            return linker;
        }

        var arguments = new List<string>();
        if (candidate.ToolSet.Kind == ToolKind.Gnu)
        {
            if (GnuArchitectureArgument(candidate.Layout) is string architectureArgument)
            {
                arguments.Add(architectureArgument);
            }
        }
        else
        {
            arguments.Add("--target=" + candidate.TargetTriple);
        }

        if (candidate.Layout.SysrootPath is string sysroot)
        {
            arguments.AddRange(candidate.Sdk?.Kind == SdkKind.Apple ? ["-isysroot", sysroot] : ["--sysroot=" + sysroot]);
        }

        arguments.Add("-print-prog-name=" + name);
        ProcessResult? result = await new DiscoveryContext().ProbeAsync(candidate.CCompiler.Path, arguments, cancellationToken).ConfigureAwait(false);
        string? path = result?.StandardOutput.Trim();
        return path is not null && Path.IsPathFullyQualified(path) && File.Exists(path) ? new Tool(name, path) : null;
    }

    private static void Skip(AutoTestRun run, string? path, string reason)
    {
        var diagnostic = new Diagnostic(DiagnosticSeverity.Info, "autotest-candidate-skipped", "AutoTest", reason, path);
        run.Diagnostics.Add(diagnostic);
        Console.WriteLine($"  Skip {path}: {reason}");
    }

    private static string Describe(TargetLayout layout) =>
        $"{layout.Platform}/{layout.Architecture}, triple={layout.TargetTriple ?? "unspecified"}, variant={layout.Multilib ?? "unclassified"}";

    private static void VerifyMinimumCount(AutoTestCommand command, AutoTestRun run)
    {
        if (run.DiscoveredInstallationCount < command.MinimumCount)
        {
            throw new AutoTestFailureException($"Found {run.DiscoveredInstallationCount} matching installations; {command.MinimumCount} required.");
        }
    }

    private static async Task VerifySelectionAsync(
        AutoTestCommand command, SmokeConfiguration configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyInventory(configuration.Sdk!, configuration.Layout, cancellationToken);
        if (configuration.MsvcSdk is not null && configuration.MsvcLayout is not null)
        {
            VerifyInventory(configuration.MsvcSdk, configuration.MsvcLayout, cancellationToken);
        }

        if (configuration.CompilerSdk is not null && configuration.CompilerLayout is not null)
        {
            VerifyInventory(configuration.CompilerSdk, configuration.CompilerLayout, cancellationToken);
        }

        // Re-open only the selected target and variant through the platform SDK's own explicit root.
        Sdk sdk = configuration.Sdk!;
        Incant.Core.Cpp.FindSdk.DiscoveryResult result = await SdkFinder.CreateDefault().FindSdksAsync(new SdkQuery
        {
            Kind = sdk.Kind,
            RootPath = sdk.RootPath,
            CompilerPath = sdk.Kind is SdkKind.Linux or SdkKind.Sysroot ? configuration.CCompiler.Path : null,
            TargetPlatform = configuration.TargetPlatform,
            TargetArchitecture = configuration.TargetArchitecture,
            TargetTriple = configuration.Layout.TargetTriple,
            Multilib = configuration.Layout.Multilib,
            SysrootPath = sdk.Kind is SdkKind.Linux or SdkKind.Sysroot ? configuration.Layout.SysrootPath : null,
            Version = sdk.Version is null ? null : new VersionConstraint(exact: sdk.Version),
            IncludePreview = command.IncludePreview,
        }, cancellationToken).ConfigureAwait(false);
        VerifyDiscoveryDiagnostics(result.Diagnostics);
        Sdk explicitSdk = result.Sdks.FirstOrDefault()
            ?? throw new AutoTestFailureException("The selected platform SDK could not be rediscovered by its explicit root.");
        TargetLayout explicitLayout = explicitSdk.Layouts.FirstOrDefault(layout =>
            layout.Multilib == configuration.Layout.Multilib && layout.SysrootPath == configuration.Layout.SysrootPath)
            ?? throw new AutoTestFailureException("Explicit SDK discovery lost the selected target layout.");
        VerifyInventory(explicitSdk, explicitLayout, cancellationToken);
        if (!explicitSdk.Sources.Contains(Source.Explicit))
        {
            throw new AutoTestFailureException("Explicit SDK discovery lost its provenance.");
        }

        // Verify invalid explicit input without writing to the installation.
        string missingRoot = Path.Combine(configuration.ToolSet.RootPath, "incant-missing-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ToolFinder.CreateDefault().FindToolSetsAsync(
                new ToolSetQuery { Kind = configuration.ToolSet.Kind, RootPath = missingRoot }, cancellationToken).ConfigureAwait(false);
            throw new AutoTestFailureException("An invalid explicit tool root silently fell back to an installed environment.");
        }
        catch (DiscoveryException)
        {
        }
    }

    internal static async Task<Tool?> FindComponentAsync(
        ToolSet toolSet, ComponentKind component, TargetArchitecture architecture, TargetPlatform? platform = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] names = (toolSet.Kind, component) switch
        {
            (ToolKind.VisualStudio, ComponentKind.Compiler or ComponentKind.CppCompiler) => [ToolNames.Cl],
            (ToolKind.VisualStudio, ComponentKind.Linker) => [ToolNames.Link],
            (ToolKind.VisualStudio, ComponentKind.Archiver) => [ToolNames.Lib],
            (ToolKind.Gnu, ComponentKind.Compiler) => [ToolNames.Gcc],
            (ToolKind.Gnu, ComponentKind.CppCompiler) => [ToolNames.Gxx],
            (ToolKind.Gnu, ComponentKind.Archiver) => [ToolNames.GccAr, ToolNames.Ar],
            (ToolKind.Gnu, ComponentKind.Linker) => [ToolNames.Ld],
            (ToolKind.Emscripten, ComponentKind.Compiler or ComponentKind.Linker) => [ToolNames.Emcc],
            (ToolKind.Emscripten, ComponentKind.CppCompiler) => [ToolNames.Emxx],
            (ToolKind.Emscripten, ComponentKind.Archiver) => [ToolNames.Emar],
            (ToolKind.Emscripten, ComponentKind.Ranlib) => [ToolNames.Emranlib],
            (_, ComponentKind.Compiler) => [ToolNames.Clang],
            (_, ComponentKind.CppCompiler) => [ToolNames.Clangxx],
            (ToolKind.Xcode, ComponentKind.Archiver) => [ToolNames.Ar],
            (ToolKind.Xcode, ComponentKind.Linker) => [ToolNames.Ld],
            (_, ComponentKind.Archiver) => [ToolNames.LlvmAr, ToolNames.LlvmLib],
            (_, ComponentKind.Linker) => [ToolNames.LdLld, ToolNames.LldLink, ToolNames.WasmLd, "ld64.lld"],
            (_, ComponentKind.Ranlib) => [ToolNames.LlvmRanlib, ToolNames.Ranlib],
            _ => [],
        };
        foreach (string name in names)
        {
            Tool? tool = await toolSet.FindToolAsync(name,
                new ToolQuery { TargetArchitecture = architecture, TargetPlatform = platform }, cancellationToken).ConfigureAwait(false);
            if (tool is not null)
            {
                return tool;
            }
        }

        return null;
    }

    private static void VerifyInventory(Sdk sdk, TargetLayout layout, CancellationToken cancellationToken)
    {
        foreach (Resource resource in layout.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(resource.Path)
                || !(resource.IsDirectory ? Directory.Exists(resource.Path) : File.Exists(resource.Path)))
            {
                throw new AutoTestFailureException($"SDK {sdk.Kind} reported a nonexistent resource: {resource.Path}");
            }
        }
    }

    private static TargetPlatform DefaultPlatform(ToolSet toolSet) => toolSet.Kind switch
    {
        ToolKind.VisualStudio => TargetPlatform.Windows,
        ToolKind.AndroidNdk => TargetPlatform.Android,
        ToolKind.Emscripten => TargetPlatform.Emscripten,
        ToolKind.WasiSdk => TargetPlatform.Wasi,
        _ => Platform.OS switch
        {
            PlatformOS.Windows => TargetPlatform.Windows,
            PlatformOS.OSX => TargetPlatform.MacOS,
            _ => TargetPlatform.Linux,
        },
    };

    private static TargetArchitecture DefaultArchitecture(ToolSet toolSet) => toolSet.Kind is ToolKind.WasiSdk or ToolKind.Emscripten
        ? TargetArchitecture.Wasm32 : Platform.Arch switch
        {
            PlatformArch.ARM64 => TargetArchitecture.ARM64,
            PlatformArch.X86 => TargetArchitecture.X86,
            _ => TargetArchitecture.X64,
        };

    // Deployment policy belongs to this smoke test, not either Finder.
    internal static string AppleTargetTriple(TargetLayout layout)
    {
        string architecture = layout.Architecture switch
        {
            TargetArchitecture.X64 => "x86_64",
            TargetArchitecture.ARM64 => "arm64",
            _ => throw new AutoTestFailureException("Apple smoke tests require x64 or ARM64."),
        };
        (string platform, Version baseline, bool simulator) = layout.Platform switch
        {
            TargetPlatform.MacOS => ("macos", new Version(layout.Architecture == TargetArchitecture.X64 ? 10 : 11, layout.Architecture == TargetArchitecture.X64 ? 15 : 0), false),
            TargetPlatform.IOS => ("ios", new Version(13, 0), false),
            TargetPlatform.IOSSimulator => ("ios", new Version(14, 0), true),
            TargetPlatform.TvOS => ("tvos", new Version(13, 0), false),
            TargetPlatform.TvOSSimulator => ("tvos", new Version(14, 0), true),
            TargetPlatform.WatchOS => ("watchos", new Version(7, 0), false),
            TargetPlatform.WatchOSSimulator => ("watchos", new Version(7, 0), true),
            TargetPlatform.VisionOS => ("xros", new Version(1, 0), false),
            TargetPlatform.VisionOSSimulator => ("xros", new Version(1, 0), true),
            _ => throw new AutoTestFailureException("The requested target is not an Apple platform."),
        };
        Version deployment = layout.MinimumDeploymentVersion is Version minimum && minimum > baseline ? minimum : baseline;
        return $"{architecture}-apple-{platform}{deployment}{(simulator ? "-simulator" : "")}";
    }

    private static string TargetTriple(TargetLayout layout)
    {
        if (layout.Platform is TargetPlatform.MacOS or TargetPlatform.IOS or TargetPlatform.IOSSimulator
            or TargetPlatform.TvOS or TargetPlatform.TvOSSimulator or TargetPlatform.WatchOS or TargetPlatform.WatchOSSimulator
            or TargetPlatform.VisionOS or TargetPlatform.VisionOSSimulator)
        {
            return AppleTargetTriple(layout);
        }

        if (layout.TargetTriple is not null)
        {
            return layout.TargetTriple;
        }

        string architecture = layout.Architecture switch
        {
            TargetArchitecture.X86 => "i686",
            TargetArchitecture.X64 => "x86_64",
            TargetArchitecture.ARM => "armv7a",
            TargetArchitecture.ARM64 when layout.Platform is not TargetPlatform.Windows and not TargetPlatform.Linux => "arm64",
            TargetArchitecture.ARM64 => "aarch64",
            _ => "wasm32",
        };
        string platform = layout.Platform switch
        {
            TargetPlatform.Windows => "pc-windows-msvc",
            _ => "unknown-linux-gnu",
        };
        return $"{architecture}-{platform}";
    }

    private static VersionConstraint? Major(int? major) => major is null ? null
        : new VersionConstraint(minimumInclusive: new Version(major.Value, 0), maximumExclusive: new Version(checked(major.Value + 1), 0));
}
