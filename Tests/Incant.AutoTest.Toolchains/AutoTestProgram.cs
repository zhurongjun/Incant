using Incant.Core.Cpp;

internal static class AutoTestProgram
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        AutoTestParseResult parsed = AutoTestCommandLine.Parse(arguments);
        if (parsed.Command is not AutoTestCommand command)
        {
            return parsed.ExitCode;
        }

        var runs = new List<AutoTestRun>();
        try
        {
            await RunAsync(null).ConfigureAwait(false);
            if (command.ExplicitRoot is not null)
            {
                await RunAsync(command.ExplicitRoot).ConfigureAwait(false);
            }

            if (command.JsonPath is not null)
            {
                AutoTestReportWriter.Write(command.JsonPath, command, runs, true, null);
            }

            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine(exception.Message);
            if (exception is DiscoveryException discovery)
            {
                foreach (Diagnostic diagnostic in discovery.Diagnostics)
                {
                    Console.Error.WriteLine($"{diagnostic.Provider}: {diagnostic.Message}");
                }
            }

            if (command.JsonPath is not null)
            {
                AutoTestReportWriter.Write(command.JsonPath, command, runs, false, exception.Message);
            }

            return 1;
        }

        async Task RunAsync(string? explicitRoot)
        {
            AutoTestRun run = await AutoTestDiscovery.DiscoverAsync(command, explicitRoot).ConfigureAwait(false);
            runs.Add(run);
            Console.WriteLine(run.Name);
            foreach (Incant.Core.Cpp.FindTools.ToolSet toolSet in run.ToolSets)
            {
                Console.WriteLine($"  {toolSet.Kind} {toolSet.Version} product {toolSet.ProductVersion} compiler {toolSet.CompilerVersion}");
                Console.WriteLine($"    {toolSet.RootPath} [{string.Join(',', toolSet.Sources)}]");
            }

            if (command.Operation != AutoTestOperation.Discover)
            {
                SmokeConfiguration configuration = await AutoTestDiscovery.ConfigureAsync(command, run, explicitRoot).ConfigureAwait(false);
                run.Configuration = configuration;
                run.SmokeTests = command.Operation == AutoTestOperation.VerifyClangCl
                    ? await ToolchainSmokeTester.RunClangClAsync(configuration, command.ClangClLinker!.Value).ConfigureAwait(false)
                    : await ToolchainSmokeTester.RunAsync(configuration).ConfigureAwait(false);
                foreach (ToolchainSmokeResult result in run.SmokeTests)
                {
                    Console.WriteLine($"  {result.Language}: compiled={result.CompilationSucceeded}, executed={result.Executed}, success={result.IsSuccess}");
                    if (result.Error is not null)
                    {
                        Console.Error.WriteLine(result.Error + Environment.NewLine + result.CompilationStandardError);
                    }
                }

                if (run.SmokeTests.Any(result => !result.IsSuccess))
                {
                    throw new AutoTestFailureException("C/C++ smoke verification failed.");
                }
            }

            foreach (Incant.Core.Cpp.FindSdk.Sdk sdk in run.Sdks)
            {
                Console.WriteLine($"  SDK {sdk.Kind} {sdk.Version} [{string.Join(',', sdk.Layouts.Select(layout => $"{layout.Platform}/{layout.Architecture}"))}]");
                Console.WriteLine($"    {sdk.RootPath}");
            }

            foreach (Diagnostic diagnostic in run.Diagnostics)
            {
                Console.WriteLine($"  {diagnostic.Severity} {diagnostic.Provider}: {diagnostic.Message}");
            }
        }
    }
}
