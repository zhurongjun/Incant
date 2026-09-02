using Incant.Base;
using Incant.Core.Toolchains;

/// <summary>Coordinates command parsing, host discovery, verification, smoke compilation, and reporting.</summary>
internal static class AutoTestProgram
{
    /// <summary>Runs one AutoTest command and maps command, verification, and runtime failures to exit codes.</summary>
    internal static async Task<int> RunAsync(string[] arguments)
    {
        AutoTestParseResult parseResult = AutoTestCommandLine.Parse(arguments);
        AutoTestCommand? command = parseResult.Command;
        if (command is null)
        {
            return parseResult.ExitCode;
        }

        var runs = new List<AutoTestRun>();
        try
        {
            var service = DiscoveryService.CreateDefault();
            Catalog automaticCatalog = await service.DiscoverAsync(
                CreateDiscoveryOptions(command, explicitPath: null)).ConfigureAwait(false);
            await ProcessCatalogAsync(
                command,
                runs,
                "automatic",
                "Automatic discovery",
                automaticCatalog).ConfigureAwait(false);

            if (command.ExplicitRoot is not null)
            {
                Catalog explicitCatalog = await service.DiscoverAsync(
                    CreateDiscoveryOptions(command, command.ExplicitRoot)).ConfigureAwait(false);
                await ProcessCatalogAsync(
                    command,
                    runs,
                    "explicit",
                    "Explicit-path discovery",
                    explicitCatalog).ConfigureAwait(false);
            }

            if (command.JsonPath is not null)
            {
                AutoTestReportWriter.Write(command.JsonPath, command, runs, success: true, error: null);
            }

            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine(exception.Message);
            if (command.JsonPath is not null)
            {
                AutoTestReportWriter.Write(
                    command.JsonPath,
                    command,
                    runs,
                    success: false,
                    error: exception.Message);
            }

            return 1;
        }
    }

    private static DiscoveryOptions CreateDiscoveryOptions(
        AutoTestCommand command,
        string? explicitPath)
    {
        IReadOnlyCollection<Kind>? kinds = GetDiscoveryKinds(command);
        return new DiscoveryOptions
        {
            Kinds = kinds,
            ExplicitPaths = explicitPath is null
                ? null
                : [new PathHint(command.Kind!.Value, explicitPath)],
            IncludePreview = command.IncludePreview,
            Refresh = true,
        };
    }

    // Verification discovers companion SDKs or compilers when the requested family cannot link alone.
    private static IReadOnlyCollection<Kind>? GetDiscoveryKinds(AutoTestCommand command)
    {
        if (command.Kind is not Kind kind)
        {
            return null;
        }

        if (command.Operation == AutoTestOperation.Discover)
        {
            return [kind];
        }

        return kind switch
        {
            Kind.VisualStudio => [Kind.VisualStudio, Kind.WindowsSdk],
            Kind.WindowsSdk => [Kind.WindowsSdk, Kind.VisualStudio],
            Kind.Llvm when command.Target == TargetPlatform.Windows
                || Platform.OSIsWindows =>
                [Kind.Llvm, Kind.WindowsSdk, Kind.VisualStudio],
            _ => [kind],
        };
    }

    private static async Task ProcessCatalogAsync(
        AutoTestCommand command,
        ICollection<AutoTestRun> runs,
        string runName,
        string title,
        Catalog catalog)
    {
        var run = new AutoTestRun(runName, catalog);
        runs.Add(run);
        PrintCatalog(title, catalog);
        if (command.Operation == AutoTestOperation.Discover)
        {
            return;
        }

        Profile profile = AutoTestCatalogVerifier.Verify(command, catalog, title.ToLowerInvariant());
        if (command.Operation == AutoTestOperation.VerifyClangCl)
        {
            ClangClLinker linker = command.ClangClLinker
                ?? throw new InvalidOperationException("A clang-cl verification must select a linker.");
            Console.WriteLine($"  clang-cl linker: {linker}");
            run.SmokeTests = await ToolchainSmokeTester.RunClangClAsync(
                profile,
                catalog,
                linker,
                command.MsvcMajor).ConfigureAwait(false);
        }
        else
        {
            run.SmokeTests = await ToolchainSmokeTester.RunAsync(profile, catalog).ConfigureAwait(false);
        }

        PrintSmokeTests(run.SmokeTests);

        ToolchainSmokeResult[] failures = run.SmokeTests
            .Where(result => !result.IsSuccess)
            .ToArray();
        if (failures.Length > 0)
        {
            string details = string.Join(
                Environment.NewLine,
                failures.Select(result => $"{result.Language}: {result.Error}"));
            throw new AutoTestFailureException($"Toolchain smoke verification failed:{Environment.NewLine}{details}");
        }
    }

    private static void PrintCatalog(string title, Catalog catalog)
    {
        Console.WriteLine(title);
        foreach (Installation toolchain in catalog.Installations)
        {
            Console.WriteLine(
                $"  toolchain {toolchain.Kind} {FormatVersion(toolchain.ProductVersion)} "
                + $"compiler {FormatVersion(toolchain.CompilerVersion)} "
                + $"host {toolchain.HostOS}/{toolchain.HostArchitecture} "
                + $"[{string.Join(',', toolchain.Sources)}]");
            Console.WriteLine($"    {toolchain.RootPath}");
        }

        foreach (SdkInstallation sdk in catalog.Sdks)
        {
            Console.WriteLine(
                $"  sdk {sdk.Kind}/{sdk.TargetPlatform} {FormatVersion(sdk.Version)} "
                + $"[{string.Join(',', sdk.TargetArchitectures)}]");
            Console.WriteLine($"    {sdk.SysrootPath}");
            foreach (Diagnostic diagnostic in sdk.Diagnostics)
            {
                Console.WriteLine($"    {diagnostic.Code}: {diagnostic.Message}");
            }
        }

        foreach (Diagnostic diagnostic in catalog.Diagnostics)
        {
            Console.WriteLine(
                $"  {diagnostic.Severity} {diagnostic.Provider}/{diagnostic.Code}: "
                + diagnostic.Message);
        }
    }

    private static void PrintSmokeTests(IEnumerable<ToolchainSmokeResult> results)
    {
        foreach (ToolchainSmokeResult result in results)
        {
            string execution = result.Executed
                ? result.ExecutionSucceeded == true ? "executed" : "execution failed"
                : $"not executed ({result.ExecutionSkipReason})";
            string linker = result.LinkerPath is null ? string.Empty : $", linker {result.LinkerPath}";
            Console.WriteLine(
                $"  {result.Language} HelloWorld: "
                + $"{(result.CompilationSucceeded ? "compiled" : "compilation failed")}, {execution}{linker}");
        }
    }

    private static string FormatVersion(Version? version) => version?.ToString() ?? "unknown";
}
