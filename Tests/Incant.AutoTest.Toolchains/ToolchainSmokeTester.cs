using System.ComponentModel;
using Incant.Base;
using Incant.Core.Cpp;
using Incant.Core.Cpp.FindSdk;
using Kind = Incant.Core.Cpp.FindTools.Kind;

/// <summary>Compiles and, when possible, executes C and C++ HelloWorld programs with a resolved configuration.</summary>
internal static class ToolchainSmokeTester
{
    private static readonly TimeSpan s_compileTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan s_executionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Runs both language probes and preserves both outcomes even when one probe fails.</summary>
    internal static Task<IReadOnlyList<ToolchainSmokeResult>> RunAsync(
        SmokeConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(configuration, clangClLinker: null, cancellationToken);

    /// <summary>Runs C and C++ probes through clang-cl and the selected Windows linker.</summary>
    internal static Task<IReadOnlyList<ToolchainSmokeResult>> RunClangClAsync(
        SmokeConfiguration configuration,
        ClangClLinker linker,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(configuration, linker, cancellationToken);

    private static async Task<IReadOnlyList<ToolchainSmokeResult>> RunCoreAsync(
        SmokeConfiguration configuration,
        ClangClLinker? clangClLinker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

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
                    configuration,
                    language,
                    workingDirectory,
                    clangClLinker,
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
        SmokeConfiguration configuration,
        SmokeLanguage language,
        string workingDirectory,
        ClangClLinker? clangClLinker,
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
                    configuration,
                    selectedLinker,
                    language,
                    sourcePath,
                    workingDirectory)
                : CreateCompilerInvocation(
                    configuration,
                    language,
                    sourcePath,
                    workingDirectory);
            compilerPath = invocation.ExecutablePath;
            linkerPath = invocation.LinkerPath ?? configuration.Linker?.Path;
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
                    configuration.TargetTriple,
                    compilation);
            }

            ExecutionInvocation? execution = CreateExecutionInvocation(
                configuration,
                invocation.OutputPath,
                workingDirectory);
            if (execution is null)
            {
                return new ToolchainSmokeResult(
                    languageName,
                    compilerPath,
                    linkerPath,
                    configuration.TargetTriple,
                    CompilationSucceeded: true,
                    compilation.StandardOutput,
                    compilation.StandardError,
                    Executed: false,
                    ExecutionSucceeded: null,
                    ExecutionStandardOutput: string.Empty,
                    ExecutionStandardError: string.Empty,
                    GetExecutionSkipReason(configuration),
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
                configuration.TargetTriple,
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
        catch (Exception exception) when (exception is AutoTestFailureException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new ToolchainSmokeResult(
                languageName,
                compilerPath,
                linkerPath,
                configuration.TargetTriple,
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

    // Each platform invocation is built exclusively from the resolved run rather than ambient compiler flags.
    private static CompilerInvocation CreateCompilerInvocation(
        SmokeConfiguration configuration,
        SmokeLanguage language,
        string sourcePath,
        string workingDirectory)
    {
        string compilerPath = (language == SmokeLanguage.C ? configuration.CCompiler : configuration.CppCompiler).Path;
        string outputPath = Path.Combine(
            workingDirectory,
            GetOutputFileName(configuration, language));
        var options = new ProcessOptions
        {
            WorkingDirectory = workingDirectory,
            Timeout = s_compileTimeout,
        };

        return configuration.ToolSet.Kind switch
        {
            Kind.VisualStudio => CreateMsvcInvocation(
                configuration,
                compilerPath,
                language,
                sourcePath,
                outputPath,
                options),
            Kind.Gnu => new CompilerInvocation(
                compilerPath,
                CreateGnuArguments(configuration, language, sourcePath, outputPath),
                outputPath,
                options),
            Kind.Llvm when configuration.TargetPlatform == TargetPlatform.Windows =>
                CreateWindowsClangInvocation(
                    configuration,
                    compilerPath,
                    language,
                    sourcePath,
                    outputPath,
                    options),
            Kind.Llvm => new CompilerInvocation(
                compilerPath,
                CreateClangArguments(configuration, language, sourcePath, outputPath),
                outputPath,
                options),
            Kind.Xcode => CreateXcodeInvocation(
                configuration,
                compilerPath,
                language,
                sourcePath,
                outputPath,
                options),
            Kind.AndroidNdk => CreateAndroidInvocation(
                configuration,
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
                configuration,
                compilerPath,
                language,
                sourcePath,
                outputPath,
                options),
            _ => throw new AutoTestFailureException(
                $"Smoke compilation is not implemented for {configuration.ToolSet.Kind}."),
        };
    }

    private static CompilerInvocation CreateClangClInvocation(
        SmokeConfiguration configuration,
        ClangClLinker linker,
        SmokeLanguage language,
        string sourcePath,
        string workingDirectory)
    {
        if (!Enum.IsDefined(linker))
        {
            throw new ArgumentOutOfRangeException(nameof(linker), linker, null);
        }

        if (configuration.ToolSet.Kind != Kind.Llvm
            || configuration.TargetPlatform != TargetPlatform.Windows)
        {
            throw new AutoTestFailureException(
                "clang-cl verification requires a resolved LLVM Windows configuration.");
        }

        string compilerPath = (language == SmokeLanguage.C ? configuration.CCompiler : configuration.CppCompiler).Path;
        WindowsCompilationLayout layout = CreateWindowsCompilationLayout(configuration);
        string linkerPath = configuration.Linker?.Path
            ?? throw new AutoTestFailureException("No Windows linker was selected.");
        string outputPath = Path.Combine(
            workingDirectory,
            GetOutputFileName(configuration, language));
        var arguments = new List<string>
        {
            "/nologo",
            "/MT",
            language == SmokeLanguage.C ? "/TC" : "/TP",
            language == SmokeLanguage.C ? "/std:c11" : "/std:c++17",
            $"/clang:--target={configuration.TargetTriple}",
            $"/clang:--ld-path={linkerPath}",
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
        SmokeConfiguration configuration,
        string compilerPath,
        SmokeLanguage language,
        string sourcePath,
        string outputPath,
        ProcessOptions baseOptions)
    {
        WindowsCompilationLayout layout = CreateWindowsCompilationLayout(configuration);
        var arguments = new List<string>
        {
            "/nologo",
            "/MT",
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
            WithPath(baseOptions, Path.GetDirectoryName(compilerPath)!, layout.ToolBinaryDirectory),
            configuration.Linker!.Path);
    }

    private static CompilerInvocation CreateWindowsClangInvocation(
        SmokeConfiguration configuration,
        string compilerPath,
        SmokeLanguage language,
        string sourcePath,
        string outputPath,
        ProcessOptions baseOptions)
    {
        WindowsCompilationLayout layout = CreateWindowsCompilationLayout(configuration);
        List<string> arguments = CreateClangArguments(configuration, language, sourcePath, outputPath).ToList();
        arguments.Add("-fms-runtime-lib=static");
        arguments.Add("-fuse-ld=lld");
        arguments.Add("--ld-path=" + configuration.Linker!.Path);
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
                layout.ToolBinaryDirectory),
            configuration.Linker!.Path);
    }

    private static CompilerInvocation CreateXcodeInvocation(
        SmokeConfiguration configuration,
        string compilerPath,
        SmokeLanguage language,
        string sourcePath,
        string outputPath,
        ProcessOptions options)
    {
        string sysroot = configuration.Layout.SysrootPath
            ?? throw new AutoTestFailureException("The selected Xcode configuration has no SDK.");
        List<string> arguments = CreateLanguageStandardArguments(language);
        arguments.AddRange(
        [
            "-target",
            CreateAppleSmokeTargetTriple(configuration),
            "-isysroot",
            sysroot,
            sourcePath,
            "-o",
            outputPath,
        ]);
        return new CompilerInvocation(compilerPath, arguments, outputPath, options);
    }

    private static CompilerInvocation CreateAndroidInvocation(
        SmokeConfiguration configuration,
        string compilerPath,
        SmokeLanguage language,
        string sourcePath,
        string outputPath,
        ProcessOptions options)
    {
        string sysroot = configuration.Layout.SysrootPath
            ?? throw new AutoTestFailureException("The selected Android NDK configuration has no SDK.");
        int minimumApi = configuration.TargetArchitecture is
            TargetArchitecture.ARM64 or TargetArchitecture.X64
                ? 21
                : 16;
        int apiLevel = configuration.Layout.ApiLevels.FirstOrDefault(level => level >= minimumApi);
        if (apiLevel == 0)
        {
            throw new AutoTestFailureException(
                $"The selected Android NDK has no API level at or above {minimumApi}.");
        }

        List<string> arguments = CreateLanguageStandardArguments(language);
        arguments.AddRange(
        [
            $"--target={configuration.TargetTriple}{apiLevel}",
            $"--sysroot={sysroot}",
            sourcePath,
            "-o",
            outputPath,
        ]);
        return new CompilerInvocation(compilerPath, arguments, outputPath, options);
    }

    private static CompilerInvocation CreateWasiInvocation(
        SmokeConfiguration configuration,
        string compilerPath,
        SmokeLanguage language,
        string sourcePath,
        string outputPath,
        ProcessOptions options)
    {
        string sysroot = configuration.Layout.SysrootPath
            ?? throw new AutoTestFailureException("The selected WASI configuration has no SDK.");
        List<string> arguments = CreateLanguageStandardArguments(language);
        arguments.AddRange(
        [
            $"--target={configuration.TargetTriple}",
            $"--sysroot={sysroot}",
            sourcePath,
            "-o",
            outputPath,
        ]);
        return new CompilerInvocation(compilerPath, arguments, outputPath, options);
    }

    private static IReadOnlyList<string> CreateGnuArguments(
        SmokeConfiguration configuration, SmokeLanguage language, string sourcePath, string outputPath)
    {
        List<string> arguments = CreateUnixCompilerArguments(language, sourcePath, outputPath).ToList();
        if (AutoTestDiscovery.GnuArchitectureArgument(configuration.Layout) is string architectureArgument)
        {
            arguments.Add(architectureArgument);
        }

        if (configuration.Layout.SysrootPath is string sysroot)
        {
            arguments.AddRange(configuration.Sdk?.Kind == Incant.Core.Cpp.FindSdk.Kind.Apple
                ? ["-isysroot", sysroot] : ["--sysroot=" + sysroot]);
        }

        return arguments;
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
        SmokeConfiguration configuration,
        SmokeLanguage language,
        string sourcePath,
        string outputPath)
    {
        List<string> arguments = CreateLanguageStandardArguments(language);
        string targetTriple = configuration.TargetPlatform == TargetPlatform.MacOS
            ? CreateAppleSmokeTargetTriple(configuration)
            : configuration.TargetTriple;
        arguments.Add($"--target={targetTriple}");
        if (configuration.Layout.SysrootPath is string sysroot)
        {
            arguments.AddRange(configuration.Sdk?.Kind == Incant.Core.Cpp.FindSdk.Kind.Apple
                ? ["-isysroot", sysroot] : ["--sysroot=" + sysroot]);
        }

        arguments.AddRange([sourcePath, "-o", outputPath]);
        return arguments;
    }

    private static List<string> CreateLanguageStandardArguments(SmokeLanguage language) =>
        [language == SmokeLanguage.C ? "-std=c11" : "-std=c++17"];

    // Only the upper layer composes SDK inventories into include/library arguments.
    private static WindowsCompilationLayout CreateWindowsCompilationLayout(SmokeConfiguration configuration)
    {
        TargetLayout msvcLayout = configuration.MsvcLayout
            ?? throw new AutoTestFailureException("The selected Windows configuration has no MSVC development files.");
        Resource[] resources = msvcLayout.Resources.Concat(configuration.Layout.Resources).ToArray();
        string[] includes = resources.Where(resource => resource.Purpose == ResourcePurpose.CppInclude)
            .Select(resource => resource.Path).Distinct().ToArray();
        string[] libraries = resources.Where(resource => resource.Purpose == ResourcePurpose.LibraryDirectory)
            .Select(resource => resource.Path).Distinct().ToArray();
        string linker = configuration.Linker?.Path
            ?? throw new AutoTestFailureException("The selected Windows configuration has no linker.");
        return new WindowsCompilationLayout(includes, libraries, Path.GetDirectoryName(linker)!);
    }

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
        SmokeConfiguration configuration,
        string outputPath,
        string workingDirectory)
    {
        var options = new ProcessOptions
        {
            WorkingDirectory = workingDirectory,
            Timeout = s_executionTimeout,
        };
        if (CanRunNatively(configuration))
        {
            return new ExecutionInvocation(outputPath, [], options);
        }

        if (configuration.TargetPlatform == TargetPlatform.Emscripten)
        {
            string? node = ResolveExecutable("node");
            return node is null ? null : new ExecutionInvocation(node, [outputPath], options);
        }

        if (configuration.TargetPlatform == TargetPlatform.Wasi)
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

    private static string GetExecutionSkipReason(SmokeConfiguration configuration)
    {
        if (configuration.TargetPlatform == TargetPlatform.Emscripten)
        {
            return "Node.js was not found on PATH";
        }

        if (configuration.TargetPlatform == TargetPlatform.Wasi)
        {
            return "no WASI runtime was found on PATH";
        }

        return "the target cannot execute directly on this host";
    }

    private static bool CanRunNatively(SmokeConfiguration configuration)
    {
        TargetPlatform currentPlatform = Platform.OS switch
        {
            PlatformOS.Windows => TargetPlatform.Windows,
            PlatformOS.Linux => TargetPlatform.Linux,
            PlatformOS.OSX => TargetPlatform.MacOS,
            _ => TargetPlatform.Unknown,
        };
        TargetArchitecture currentArchitecture = GetCurrentArchitecture();
        bool architectureCanRun = configuration.TargetArchitecture == currentArchitecture
            || Platform.OSIsWindows
                && currentArchitecture == TargetArchitecture.X64
                && configuration.TargetArchitecture == TargetArchitecture.X86;
        return configuration.TargetPlatform == currentPlatform && architectureCanRun;
    }

    private static TargetArchitecture GetCurrentArchitecture() => Platform.Arch switch
    {
        PlatformArch.X86 => TargetArchitecture.X86,
        PlatformArch.X64 => TargetArchitecture.X64,
        PlatformArch.ARM64 => TargetArchitecture.ARM64,
        _ => TargetArchitecture.Unknown,
    };

    private static string CreateAppleSmokeTargetTriple(SmokeConfiguration configuration) =>
        AutoTestDiscovery.AppleTargetTriple(configuration.Layout);

    private static string GetOutputFileName(SmokeConfiguration configuration, SmokeLanguage language)
    {
        string prefix = language == SmokeLanguage.C ? "hello-c" : "hello-cpp";
        return configuration.TargetPlatform switch
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
