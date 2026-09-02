using Incant.Base;
using Incant.Core.Toolchains;

/// <summary>Compiles and, when possible, executes C and C++ HelloWorld programs with a resolved profile.</summary>
internal static class ToolchainSmokeTester
{
    private static readonly TimeSpan s_compileTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_executionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Runs both language probes and preserves both outcomes even when one probe fails.</summary>
    internal static Task<IReadOnlyList<ToolchainSmokeResult>> RunAsync(
        Profile profile,
        Catalog catalog,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(profile, catalog, clangClLinker: null, msvcMajor: null, cancellationToken);

    /// <summary>Runs C and C++ probes through clang-cl and the selected Windows linker.</summary>
    internal static Task<IReadOnlyList<ToolchainSmokeResult>> RunClangClAsync(
        Profile profile,
        Catalog catalog,
        ClangClLinker linker,
        int? msvcMajor,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(profile, catalog, linker, msvcMajor, cancellationToken);

    private static async Task<IReadOnlyList<ToolchainSmokeResult>> RunCoreAsync(
        Profile profile,
        Catalog catalog,
        ClangClLinker? clangClLinker,
        int? msvcMajor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(catalog);

        string workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "Incant.AutoTest.Toolchains",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        try
        {
            var results = new List<ToolchainSmokeResult>(capacity: 2);
            foreach (SmokeLanguage language in new[] { SmokeLanguage.C, SmokeLanguage.Cpp })
            {
                results.Add(await RunLanguageAsync(
                    profile,
                    catalog,
                    language,
                    workingDirectory,
                    clangClLinker,
                    msvcMajor,
                    cancellationToken).ConfigureAwait(false));
            }

            return Array.AsReadOnly(results.ToArray());
        }
        finally
        {
            // Smoke artifacts are disposable diagnostics and must not accumulate on long-lived runners.
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<ToolchainSmokeResult> RunLanguageAsync(
        Profile profile,
        Catalog catalog,
        SmokeLanguage language,
        string workingDirectory,
        ClangClLinker? clangClLinker,
        int? msvcMajor,
        CancellationToken cancellationToken)
    {
        string languageName = language == SmokeLanguage.C ? "C" : "C++";
        string marker = language == SmokeLanguage.C
            ? "Hello from Incant C"
            : "Hello from Incant C++";
        string sourcePath = Path.Combine(
            workingDirectory,
            language == SmokeLanguage.C ? "hello.c" : "hello.cpp");
        await File.WriteAllTextAsync(
            sourcePath,
            CreateSource(language, marker),
            cancellationToken).ConfigureAwait(false);

        string compilerPath = "unresolved";
        string? linkerPath = null;
        try
        {
            CompilerInvocation invocation = clangClLinker is ClangClLinker selectedLinker
                ? CreateClangClInvocation(
                    profile,
                    catalog,
                    selectedLinker,
                    msvcMajor,
                    language,
                    sourcePath,
                    workingDirectory)
                : CreateCompilerInvocation(
                    profile,
                    catalog,
                    language,
                    sourcePath,
                    workingDirectory);
            compilerPath = invocation.ExecutablePath;
            linkerPath = invocation.LinkerPath;
            ProcessResult compilation = await RunToolAsync(
                invocation.ExecutablePath,
                invocation.Arguments,
                invocation.Options,
                cancellationToken).ConfigureAwait(false);
            if (!compilation.IsSuccess)
            {
                return CreateCompilationFailure(
                    languageName,
                    compilerPath,
                    linkerPath,
                    profile.TargetTriple,
                    compilation);
            }

            ExecutionInvocation? execution = CreateExecutionInvocation(
                profile,
                invocation.OutputPath,
                workingDirectory);
            if (execution is null)
            {
                return new ToolchainSmokeResult(
                    languageName,
                    compilerPath,
                    linkerPath,
                    profile.TargetTriple,
                    CompilationSucceeded: true,
                    compilation.StandardOutput,
                    compilation.StandardError,
                    Executed: false,
                    ExecutionSucceeded: null,
                    ExecutionStandardOutput: string.Empty,
                    ExecutionStandardError: string.Empty,
                    GetExecutionSkipReason(profile),
                    Error: null);
            }

            ProcessResult executionResult = await RunToolAsync(
                execution.ExecutablePath,
                execution.Arguments,
                execution.Options,
                cancellationToken).ConfigureAwait(false);
            bool outputMatches = string.Equals(
                executionResult.StandardOutput.Trim(),
                marker,
                StringComparison.Ordinal);
            bool executionSucceeded = executionResult.IsSuccess && outputMatches;
            return new ToolchainSmokeResult(
                languageName,
                compilerPath,
                linkerPath,
                profile.TargetTriple,
                CompilationSucceeded: true,
                compilation.StandardOutput,
                compilation.StandardError,
                Executed: true,
                ExecutionSucceeded: executionSucceeded,
                executionResult.StandardOutput,
                executionResult.StandardError,
                ExecutionSkipReason: null,
                Error: executionSucceeded
                    ? null
                    : CreateExecutionError(executionResult, marker));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ToolchainSmokeResult(
                languageName,
                compilerPath,
                linkerPath,
                profile.TargetTriple,
                CompilationSucceeded: false,
                CompilationStandardOutput: string.Empty,
                CompilationStandardError: string.Empty,
                Executed: false,
                ExecutionSucceeded: null,
                ExecutionStandardOutput: string.Empty,
                ExecutionStandardError: string.Empty,
                ExecutionSkipReason: null,
                Error: exception.Message);
        }
    }

    // Each platform invocation is built exclusively from the resolved catalog rather than ambient compiler flags.
    private static CompilerInvocation CreateCompilerInvocation(
        Profile profile,
        Catalog catalog,
        SmokeLanguage language,
        string sourcePath,
        string workingDirectory)
    {
        string compilerPath = SelectComponentPath(
            profile.Installation,
            language == SmokeLanguage.C
                ? ComponentKind.Compiler
                : ComponentKind.CppCompiler,
            profile.TargetArchitecture);
        string outputPath = Path.Combine(
            workingDirectory,
            GetOutputFileName(profile, language));
        var options = new ProcessOptions
        {
            WorkingDirectory = workingDirectory,
            Timeout = s_compileTimeout,
        };

        return profile.Installation.Kind switch
        {
            Kind.VisualStudio => CreateMsvcInvocation(
                profile,
                profile.Installation,
                compilerPath,
                language,
                sourcePath,
                outputPath,
                options),
            Kind.Gnu => new CompilerInvocation(
                compilerPath,
                CreateUnixCompilerArguments(language, sourcePath, outputPath),
                outputPath,
                options),
            Kind.Llvm when profile.TargetPlatform == TargetPlatform.Windows =>
                CreateWindowsClangInvocation(
                    profile,
                    catalog,
                    compilerPath,
                    language,
                    sourcePath,
                    outputPath,
                    options),
            Kind.Llvm => new CompilerInvocation(
                compilerPath,
                CreateClangArguments(profile, language, sourcePath, outputPath),
                outputPath,
                options),
            Kind.Xcode => CreateXcodeInvocation(
                profile,
                compilerPath,
                language,
                sourcePath,
                outputPath,
                options),
            Kind.AndroidNdk => CreateAndroidInvocation(
                profile,
                compilerPath,
                language,
                sourcePath,
                outputPath,
                options),
            Kind.Emscripten => new CompilerInvocation(
                compilerPath,
                CreateUnixCompilerArguments(language, sourcePath, outputPath),
                outputPath,
                options),
            Kind.WasiSdk => CreateWasiInvocation(
                profile,
                compilerPath,
                language,
                sourcePath,
                outputPath,
                options),
            _ => throw new AutoTestFailureException(
                $"Smoke compilation is not implemented for {profile.Installation.Kind}."),
        };
    }

    private static CompilerInvocation CreateClangClInvocation(
        Profile profile,
        Catalog catalog,
        ClangClLinker linker,
        int? msvcMajor,
        SmokeLanguage language,
        string sourcePath,
        string workingDirectory)
    {
        if (profile.Installation.Kind != Kind.Llvm
            || profile.TargetPlatform != TargetPlatform.Windows)
        {
            throw new AutoTestFailureException(
                "clang-cl verification requires a resolved LLVM Windows profile.");
        }

        string compilerPath = SelectClangClPath(profile.Installation);
        Installation msvcToolchain = SelectMsvcToolchain(
            catalog,
            profile.TargetArchitecture,
            msvcMajor);
        WindowsCompilationLayout layout = CreateWindowsCompilationLayout(profile, msvcToolchain);
        string linkerPath = linker switch
        {
            ClangClLinker.Msvc => SelectComponentPath(
                msvcToolchain,
                ComponentKind.Linker,
                profile.TargetArchitecture),
            ClangClLinker.Lld => FindRequiredSiblingExecutable(compilerPath, "lld-link.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(linker), linker, null),
        };
        string outputPath = Path.Combine(
            workingDirectory,
            GetOutputFileName(profile, language));
        var arguments = new List<string>
        {
            "/nologo",
            language == SmokeLanguage.C ? "/TC" : "/TP",
            language == SmokeLanguage.C ? "/std:c11" : "/std:c++17",
            $"/clang:--target={profile.TargetTriple}",
            linker == ClangClLinker.Msvc ? "-fuse-ld=link" : "-fuse-ld=lld-link",
        };
        if (language == SmokeLanguage.Cpp)
        {
            arguments.Add("/EHsc");
        }

        arguments.Add(sourcePath);
        arguments.Add($"/Fe{outputPath}");
        arguments.AddRange(layout.IncludeDirectories.Select(directory => $"/I{directory}"));
        arguments.Add("/link");
        arguments.AddRange(layout.LibraryDirectories.Select(directory => $"/LIBPATH:{directory}"));

        var options = new ProcessOptions
        {
            WorkingDirectory = workingDirectory,
            Timeout = s_compileTimeout,
        };
        return new CompilerInvocation(
            compilerPath,
            arguments,
            outputPath,
            WithPath(
                options,
                Path.GetDirectoryName(linkerPath)!,
                Path.GetDirectoryName(compilerPath)!,
                layout.ToolBinaryDirectory),
            linkerPath);
    }

    private static CompilerInvocation CreateMsvcInvocation(
        Profile profile,
        Installation msvcToolchain,
        string compilerPath,
        SmokeLanguage language,
        string sourcePath,
        string outputPath,
        ProcessOptions baseOptions)
    {
        WindowsCompilationLayout layout = CreateWindowsCompilationLayout(profile, msvcToolchain);
        var arguments = new List<string>
        {
            "/nologo",
            language == SmokeLanguage.C ? "/TC" : "/TP",
            language == SmokeLanguage.C ? "/std:c11" : "/std:c++17",
        };
        if (language == SmokeLanguage.Cpp)
        {
            arguments.Add("/EHsc");
        }

        arguments.Add(sourcePath);
        arguments.Add($"/Fe{outputPath}");
        arguments.AddRange(layout.IncludeDirectories.Select(directory => $"/I{directory}"));
        arguments.Add("/link");
        arguments.AddRange(layout.LibraryDirectories.Select(directory => $"/LIBPATH:{directory}"));

        return new CompilerInvocation(
            compilerPath,
            arguments,
            outputPath,
            WithPath(baseOptions, Path.GetDirectoryName(compilerPath)!, layout.ToolBinaryDirectory));
    }

    private static CompilerInvocation CreateWindowsClangInvocation(
        Profile profile,
        Catalog catalog,
        string compilerPath,
        SmokeLanguage language,
        string sourcePath,
        string outputPath,
        ProcessOptions baseOptions)
    {
        Installation msvcToolchain = SelectMsvcToolchain(
            catalog,
            profile.TargetArchitecture,
            requiredProductMajor: null);
        WindowsCompilationLayout layout = CreateWindowsCompilationLayout(profile, msvcToolchain);
        List<string> arguments = CreateClangArguments(profile, language, sourcePath, outputPath).ToList();
        arguments.Add("-fuse-ld=lld");
        foreach (string includeDirectory in layout.IncludeDirectories)
        {
            arguments.Add("-isystem");
            arguments.Add(includeDirectory);
        }

        foreach (string libraryDirectory in layout.LibraryDirectories)
        {
            arguments.Add($"-L{libraryDirectory}");
        }

        return new CompilerInvocation(
            compilerPath,
            arguments,
            outputPath,
            WithPath(
                baseOptions,
                Path.GetDirectoryName(compilerPath)!,
                layout.ToolBinaryDirectory));
    }

    private static CompilerInvocation CreateXcodeInvocation(
        Profile profile,
        string compilerPath,
        SmokeLanguage language,
        string sourcePath,
        string outputPath,
        ProcessOptions options)
    {
        SdkInstallation sdk = profile.Sdk
            ?? throw new AutoTestFailureException("The selected Xcode profile has no SDK.");
        List<string> arguments = CreateLanguageStandardArguments(language);
        arguments.AddRange(
        [
            "-target",
            CreateAppleSmokeTargetTriple(profile),
            "-isysroot",
            sdk.SysrootPath,
            sourcePath,
            "-o",
            outputPath,
        ]);
        return new CompilerInvocation(compilerPath, arguments, outputPath, options);
    }

    private static CompilerInvocation CreateAndroidInvocation(
        Profile profile,
        string compilerPath,
        SmokeLanguage language,
        string sourcePath,
        string outputPath,
        ProcessOptions options)
    {
        SdkInstallation sdk = profile.Sdk
            ?? throw new AutoTestFailureException("The selected Android NDK profile has no SDK.");
        int minimumApi = profile.TargetArchitecture is
            TargetArchitecture.ARM64 or TargetArchitecture.X64
                ? 21
                : 16;
        int apiLevel = sdk.SupportedApiLevels.FirstOrDefault(level => level >= minimumApi);
        if (apiLevel == 0)
        {
            throw new AutoTestFailureException(
                $"The selected Android NDK has no API level at or above {minimumApi}.");
        }

        List<string> arguments = CreateLanguageStandardArguments(language);
        arguments.AddRange(
        [
            $"--target={profile.TargetTriple}{apiLevel}",
            $"--sysroot={sdk.SysrootPath}",
            sourcePath,
            "-o",
            outputPath,
        ]);
        return new CompilerInvocation(compilerPath, arguments, outputPath, options);
    }

    private static CompilerInvocation CreateWasiInvocation(
        Profile profile,
        string compilerPath,
        SmokeLanguage language,
        string sourcePath,
        string outputPath,
        ProcessOptions options)
    {
        SdkInstallation sdk = profile.Sdk
            ?? throw new AutoTestFailureException("The selected WASI profile has no SDK.");
        List<string> arguments = CreateLanguageStandardArguments(language);
        arguments.AddRange(
        [
            $"--target={profile.TargetTriple}",
            $"--sysroot={sdk.SysrootPath}",
            sourcePath,
            "-o",
            outputPath,
        ]);
        return new CompilerInvocation(compilerPath, arguments, outputPath, options);
    }

    private static IReadOnlyList<string> CreateUnixCompilerArguments(
        SmokeLanguage language,
        string sourcePath,
        string outputPath)
    {
        List<string> arguments = CreateLanguageStandardArguments(language);
        arguments.AddRange([sourcePath, "-o", outputPath]);
        return arguments;
    }

    private static IReadOnlyList<string> CreateClangArguments(
        Profile profile,
        SmokeLanguage language,
        string sourcePath,
        string outputPath)
    {
        List<string> arguments = CreateLanguageStandardArguments(language);
        string targetTriple = profile.TargetPlatform == TargetPlatform.MacOS
            ? CreateAppleSmokeTargetTriple(profile)
            : profile.TargetTriple;
        arguments.Add($"--target={targetTriple}");
        if (profile.TargetPlatform == TargetPlatform.MacOS
            && profile.Sdk?.Kind == Kind.Xcode)
        {
            arguments.Add("-isysroot");
            arguments.Add(profile.Sdk.SysrootPath);
        }

        arguments.AddRange([sourcePath, "-o", outputPath]);
        return arguments;
    }

    private static List<string> CreateLanguageStandardArguments(SmokeLanguage language) =>
        [language == SmokeLanguage.C ? "-std=c11" : "-std=c++17"];

    // MSVC and standalone Clang both need an explicit, matched MSVC/Windows SDK layout in unattended CI.
    private static WindowsCompilationLayout CreateWindowsCompilationLayout(
        Profile profile,
        Installation msvcToolchain)
    {
        SdkInstallation sdk = profile.Sdk
            ?? throw new AutoTestFailureException("The selected Windows profile has no Windows SDK.");
        string sdkVersionName = FindSdkVersionDirectoryName(sdk);
        string architectureName = GetWindowsArchitectureName(profile.TargetArchitecture);
        string msvcBinary = SelectComponentPath(
            msvcToolchain,
            ComponentKind.Linker,
            profile.TargetArchitecture);

        string[] includeDirectories =
        [
            Path.Combine(msvcToolchain.RootPath, "include"),
            Path.Combine(sdk.RootPath, "Include", sdkVersionName, "ucrt"),
            Path.Combine(sdk.RootPath, "Include", sdkVersionName, "shared"),
            Path.Combine(sdk.RootPath, "Include", sdkVersionName, "um"),
            Path.Combine(sdk.RootPath, "Include", sdkVersionName, "winrt"),
        ];
        string[] libraryDirectories =
        [
            Path.Combine(msvcToolchain.RootPath, "lib", architectureName),
            Path.Combine(sdk.RootPath, "Lib", sdkVersionName, "ucrt", architectureName),
            Path.Combine(sdk.RootPath, "Lib", sdkVersionName, "um", architectureName),
        ];
        string? missingPath = includeDirectories
            .Concat(libraryDirectories)
            .FirstOrDefault(path => !Directory.Exists(path));
        if (missingPath is not null)
        {
            throw new AutoTestFailureException(
                $"The selected Windows profile is missing '{missingPath}'.");
        }

        return new WindowsCompilationLayout(
            includeDirectories,
            libraryDirectories,
            Path.GetDirectoryName(msvcBinary)!);
    }

    private static string FindSdkVersionDirectoryName(SdkInstallation sdk)
    {
        if (sdk.Version is null)
        {
            throw new AutoTestFailureException("The selected Windows SDK has no version.");
        }

        string includeRoot = Path.Combine(sdk.RootPath, "Include");
        try
        {
            return Directory.EnumerateDirectories(includeRoot)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => Version.TryParse(name, out Version? version)
                    && version == sdk.Version)
                ?? throw new AutoTestFailureException(
                    $"Windows SDK {sdk.Version} has no matching include directory.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AutoTestFailureException(
                $"Windows SDK include directories could not be enumerated: {exception.Message}");
        }
    }

    private static string SelectComponentPath(
        Installation toolchain,
        ComponentKind kind,
        TargetArchitecture targetArchitecture)
    {
        TargetArchitecture hostArchitecture = GetCurrentArchitecture();
        Component? component = toolchain.Components
            .Where(candidate => candidate.Kind == kind)
            .Where(candidate => CanRunHostComponent(candidate.HostArchitecture, hostArchitecture))
            .Where(candidate => candidate.TargetArchitecture is TargetArchitecture.Unknown
                || candidate.TargetArchitecture == targetArchitecture)
            .OrderBy(candidate => candidate.TargetArchitecture == targetArchitecture ? 0 : 1)
            .ThenBy(candidate => candidate.HostArchitecture == hostArchitecture ? 0 : 1)
            .ThenBy(candidate => candidate.HostArchitecture == toolchain.HostArchitecture ? 0 : 1)
            .FirstOrDefault();
        return component?.Path
            ?? throw new AutoTestFailureException(
                $"The selected {toolchain.Kind} profile has no runnable {kind} component "
                + $"for {targetArchitecture}.");
    }

    private static string SelectClangClPath(Installation toolchain)
    {
        string? compiler = toolchain.Components
            .Where(component => component.Kind is
                ComponentKind.Compiler or ComponentKind.CppCompiler)
            .Select(component => component.Path)
            .FirstOrDefault(path =>
            {
                string name = Path.GetFileNameWithoutExtension(path);
                return name.Equals("clang-cl", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("clang-cl-", StringComparison.OrdinalIgnoreCase);
            });
        return compiler ?? throw new AutoTestFailureException(
            "The selected LLVM installation does not expose clang-cl.exe.");
    }

    private static Installation SelectMsvcToolchain(
        Catalog catalog,
        TargetArchitecture targetArchitecture,
        int? requiredProductMajor)
    {
        Installation? toolchain = catalog.Installations
            .Where(candidate => candidate.Kind == Kind.VisualStudio)
            .Where(candidate => candidate.TargetArchitectures.Contains(targetArchitecture))
            .Where(candidate => requiredProductMajor is null
                || candidate.ProductVersion?.Major == requiredProductMajor)
            .OrderByDescending(candidate => candidate.ProductVersion)
            .ThenByDescending(candidate => candidate.CompilerVersion)
            .FirstOrDefault();
        if (toolchain is not null)
        {
            return toolchain;
        }

        string versionRequirement = requiredProductMajor is int major
            ? $" major version {major}"
            : string.Empty;
        throw new AutoTestFailureException(
            $"clang-cl verification requires a Visual Studio{versionRequirement} toolset "
            + $"that targets {targetArchitecture}.");
    }

    private static string FindRequiredSiblingExecutable(string executablePath, string siblingName)
    {
        string sibling = Path.Combine(Path.GetDirectoryName(executablePath)!, siblingName);
        return File.Exists(sibling)
            ? sibling
            : throw new AutoTestFailureException(
                $"The selected LLVM installation does not contain '{siblingName}'.");
    }

    private static bool CanRunHostComponent(
        TargetArchitecture componentArchitecture,
        TargetArchitecture hostArchitecture) =>
        componentArchitecture is TargetArchitecture.Unknown
        || componentArchitecture == hostArchitecture
        || Platform.OSIsWindows
            && hostArchitecture == TargetArchitecture.X64
            && componentArchitecture == TargetArchitecture.X86
        || Platform.OSIsWindows
            && hostArchitecture == TargetArchitecture.ARM64
            && componentArchitecture is TargetArchitecture.X64 or TargetArchitecture.X86
        || Platform.OSIsOSX
            && hostArchitecture == TargetArchitecture.ARM64
            && componentArchitecture == TargetArchitecture.X64;

    private static ProcessOptions WithPath(ProcessOptions options, params string[] directories)
    {
        string inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string combinedPath = string.Join(
            Path.PathSeparator,
            directories.Where(path => !string.IsNullOrWhiteSpace(path)).Append(inheritedPath));
        return new ProcessOptions
        {
            WorkingDirectory = options.WorkingDirectory,
            Timeout = options.Timeout,
            StandardOutputEncoding = options.StandardOutputEncoding,
            StandardErrorEncoding = options.StandardErrorEncoding,
            EnsureUnixExecutablePermission = options.EnsureUnixExecutablePermission,
            Environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["PATH"] = combinedPath,
            },
        };
    }

    // Cross-compiled outputs still prove compile and link behavior; execution is added only when a runtime is available.
    private static ExecutionInvocation? CreateExecutionInvocation(
        Profile profile,
        string outputPath,
        string workingDirectory)
    {
        var options = new ProcessOptions
        {
            WorkingDirectory = workingDirectory,
            Timeout = s_executionTimeout,
        };
        if (CanRunNatively(profile))
        {
            return new ExecutionInvocation(outputPath, [], options);
        }

        if (profile.TargetPlatform == TargetPlatform.Emscripten)
        {
            string? node = ResolveExecutable("node");
            return node is null ? null : new ExecutionInvocation(node, [outputPath], options);
        }

        if (profile.TargetPlatform == TargetPlatform.Wasi)
        {
            string? wasmtime = ResolveExecutable("wasmtime");
            if (wasmtime is not null)
            {
                return new ExecutionInvocation(wasmtime, [outputPath], options);
            }

            string? wasmer = ResolveExecutable("wasmer");
            if (wasmer is not null)
            {
                return new ExecutionInvocation(wasmer, ["run", outputPath], options);
            }
        }

        return null;
    }

    private static string GetExecutionSkipReason(Profile profile)
    {
        if (profile.TargetPlatform == TargetPlatform.Emscripten)
        {
            return "Node.js was not found on PATH";
        }

        if (profile.TargetPlatform == TargetPlatform.Wasi)
        {
            return "no WASI runtime was found on PATH";
        }

        return "the target cannot execute directly on this host";
    }

    private static bool CanRunNatively(Profile profile)
    {
        TargetPlatform currentPlatform = Platform.OS switch
        {
            PlatformOS.Windows => TargetPlatform.Windows,
            PlatformOS.Linux => TargetPlatform.Linux,
            PlatformOS.OSX => TargetPlatform.MacOS,
            _ => TargetPlatform.Unknown,
        };
        TargetArchitecture currentArchitecture = GetCurrentArchitecture();
        bool architectureCanRun = profile.TargetArchitecture == currentArchitecture
            || Platform.OSIsWindows
                && currentArchitecture == TargetArchitecture.X64
                && profile.TargetArchitecture == TargetArchitecture.X86;
        return profile.TargetPlatform == currentPlatform && architectureCanRun;
    }

    private static TargetArchitecture GetCurrentArchitecture() => Platform.Arch switch
    {
        PlatformArch.X86 => TargetArchitecture.X86,
        PlatformArch.X64 => TargetArchitecture.X64,
        PlatformArch.ARM64 => TargetArchitecture.ARM64,
        _ => TargetArchitecture.Unknown,
    };

    private static string GetWindowsArchitectureName(TargetArchitecture architecture) =>
        architecture switch
        {
            TargetArchitecture.X86 => "x86",
            TargetArchitecture.X64 => "x64",
            TargetArchitecture.ARM64 => "arm64",
            _ => throw new AutoTestFailureException(
                $"Windows smoke compilation does not support {architecture}."),
        };

    // Explicit deployment versions keep Apple cross-linking independent of ambient Xcode settings.
    private static string CreateAppleSmokeTargetTriple(Profile profile)
    {
        string architecture = profile.TargetArchitecture switch
        {
            TargetArchitecture.X64 => "x86_64",
            TargetArchitecture.ARM64 => "arm64",
            _ => throw new AutoTestFailureException(
                $"Apple smoke compilation does not support {profile.TargetArchitecture}."),
        };
        string platform = profile.TargetPlatform switch
        {
            TargetPlatform.MacOS when profile.TargetArchitecture == TargetArchitecture.X64 =>
                "macos10.15",
            TargetPlatform.MacOS => "macos11.0",
            TargetPlatform.IOS => "ios13.0",
            TargetPlatform.IOSSimulator => "ios14.0-simulator",
            TargetPlatform.TvOS => "tvos13.0",
            TargetPlatform.TvOSSimulator => "tvos14.0-simulator",
            TargetPlatform.WatchOS => "watchos7.0",
            TargetPlatform.WatchOSSimulator => "watchos7.0-simulator",
            TargetPlatform.VisionOS => "xros1.0",
            TargetPlatform.VisionOSSimulator => "xros1.0-simulator",
            _ => throw new AutoTestFailureException(
                $"{profile.TargetPlatform} is not an Apple smoke-test target."),
        };
        return $"{architecture}-apple-{platform}";
    }

    private static string GetOutputFileName(Profile profile, SmokeLanguage language)
    {
        string prefix = language == SmokeLanguage.C ? "hello-c" : "hello-cpp";
        return profile.TargetPlatform switch
        {
            TargetPlatform.Windows => prefix + ".exe",
            TargetPlatform.Emscripten => prefix + ".js",
            TargetPlatform.Wasi => prefix + ".wasm",
            _ => prefix,
        };
    }

    private static string CreateSource(SmokeLanguage language, string marker) => language switch
    {
        SmokeLanguage.C =>
            $"#include <stdio.h>{Environment.NewLine}"
            + $"int main(void) {{ puts(\"{marker}\"); return 0; }}{Environment.NewLine}",
        SmokeLanguage.Cpp =>
            $"#include <iostream>{Environment.NewLine}"
            + $"int main() {{ std::cout << \"{marker}\\n\"; return 0; }}{Environment.NewLine}",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    private static ToolchainSmokeResult CreateCompilationFailure(
        string language,
        string compilerPath,
        string? linkerPath,
        string targetTriple,
        ProcessResult result) => new(
            language,
            compilerPath,
            linkerPath,
            targetTriple,
            CompilationSucceeded: false,
            result.StandardOutput,
            result.StandardError,
            Executed: false,
            ExecutionSucceeded: null,
            ExecutionStandardOutput: string.Empty,
            ExecutionStandardError: string.Empty,
            ExecutionSkipReason: null,
            Error: result.TimedOut
                ? "The compiler timed out."
                : $"The compiler exited with code {result.ExitCode}.");

    private static string CreateExecutionError(ProcessResult result, string expectedMarker)
    {
        if (result.TimedOut)
        {
            return "The HelloWorld program timed out.";
        }

        if (result.ExitCode != 0)
        {
            return $"The HelloWorld program exited with code {result.ExitCode}.";
        }

        return $"The HelloWorld program did not print the expected marker '{expectedMarker}'.";
    }

    // Script wrappers are adapted here, while all process lifetime and capture behavior remains in Incant.Base.Misc.
    private static async Task<ProcessResult> RunToolAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        ProcessOptions options,
        CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(executablePath);
        if (OperatingSystem.IsWindows()
            && (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)))
        {
            string commandProcessor = Environment.GetEnvironmentVariable("COMSPEC")
                ?? throw new AutoTestFailureException("COMSPEC is required to run a compiler wrapper.");
            string commandLine = string.Join(
                ' ',
                new[] { executablePath }
                    .Concat(arguments)
                    .Select(Misc.QuoteCommandLineArgument));
            return await Misc.RunProcessRawAsync(
                commandProcessor,
                $"/d /s /c \"{commandLine}\"",
                options,
                cancellationToken).ConfigureAwait(false);
        }

        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            string? python = ResolveExecutable("python") ?? ResolveExecutable("python3");
            if (python is null)
            {
                throw new AutoTestFailureException(
                    $"Python is required to run compiler wrapper '{executablePath}'.");
            }

            return await Misc.RunProcessAsync(
                python,
                new[] { executablePath }.Concat(arguments).ToArray(),
                options,
                cancellationToken).ConfigureAwait(false);
        }

        return await Misc.RunProcessAsync(
            executablePath,
            arguments,
            options,
            cancellationToken).ConfigureAwait(false);
    }

    private static string? ResolveExecutable(string name)
    {
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        foreach (string directoryValue in pathValue.Split(Path.PathSeparator))
        {
            string directory = directoryValue.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (string extension in extensions.Prepend(string.Empty))
            {
                string candidate = Path.Combine(directory, name + extension.ToLowerInvariant());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private enum SmokeLanguage
    {
        C,
        Cpp,
    }

    private sealed record CompilerInvocation(
        string ExecutablePath,
        IReadOnlyList<string> Arguments,
        string OutputPath,
        ProcessOptions Options,
        string? LinkerPath = null);

    private sealed record ExecutionInvocation(
        string ExecutablePath,
        IReadOnlyList<string> Arguments,
        ProcessOptions Options);

    private sealed record WindowsCompilationLayout(
        IReadOnlyList<string> IncludeDirectories,
        IReadOnlyList<string> LibraryDirectories,
        string ToolBinaryDirectory);
}
