using System.Text.Json;
using Incant.TestSupport;

namespace Incant.ProcessTestHelper;

internal static class CompilerCommands
{
    internal static async Task<int> RunAsync(string[] arguments, string configurationPath)
    {
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(configurationPath).ConfigureAwait(false));
        string directory = configurationPath + ".invocations";
        CompilerInvocation invocation = await CompilerInvocationStore.StartAsync(directory, arguments).ConfigureAwait(false);
        int exitCode = await ExecuteAsync(arguments, configurationPath, document.RootElement).ConfigureAwait(false);
        await CompilerInvocationStore.CompleteAsync(directory, invocation, exitCode).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task<int> ExecuteAsync(string[] arguments, string configurationPath, JsonElement configuration)
    {
        string Value(string name, string fallback = "") =>
            configuration.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? fallback : fallback;
        int Number(string name) =>
            configuration.TryGetProperty(name, out JsonElement value) ? value.GetInt32() : 0;
        string[] Paths(string name) => configuration.TryGetProperty(name, out JsonElement value)
            ? value.EnumerateArray().Select(item => item.GetString()!).ToArray() : [];

        if (configuration.TryGetProperty("ExpectedEnvironment", out JsonElement expectedEnvironment))
        {
            foreach (JsonProperty variable in expectedEnvironment.EnumerateObject())
            {
                if (Environment.GetEnvironmentVariable(variable.Name) != variable.Value.GetString())
                {
                    Console.Error.Write($"Unexpected environment variable '{variable.Name}'.");
                    return 98;
                }
            }
        }

        if (arguments.Contains("--version"))
        {
            if (Number("BlockIdentityOnce") != 0 && ClaimFirstIdentity(configurationPath))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            }

            string release = Value("ReleaseFile");
            if (release.Length > 0)
            {
                while (!File.Exists(release))
                {
                    await Task.Delay(10).ConfigureAwait(false);
                }
            }

            Console.Out.Write(Value("Identity", "clang version 18.1.8"));
            Console.Error.Write(Value("Error"));
            return Number("ExitCode");
        }

        string failingArgument = Value("FailArgument");
        if (failingArgument.Length > 0 && arguments.Contains(failingArgument))
        {
            Console.Error.Write(Value("ProbeError"));
            return Number("ProbeExitCode");
        }

        string target = Value("Target", "x86_64-linux-gnu");
        if (arguments.Any(argument => argument is "-dumpmachine" or "-print-target-triple"))
        {
            Console.WriteLine(target);
        }
        else if (arguments.Any(argument => argument is "-dumpversion" or "-dumpfullversion"))
        {
            Console.WriteLine(Value("Version", "18.1.8"));
        }
        else if (arguments.Contains("-print-multi-lib"))
        {
            Console.WriteLine(".;");
        }
        else if (arguments.Contains("-print-multi-directory"))
        {
            Console.WriteLine(".");
        }
        else if (arguments.Contains("-print-multiarch"))
        {
            Console.WriteLine(target);
        }
        else if (arguments.Contains("-print-sysroot"))
        {
            Console.WriteLine(Value("Sysroot"));
        }
        else if (arguments.Any(argument => argument is "-print-resource-dir" or "-print-file-name=include"))
        {
            Console.WriteLine(Value("Resource"));
        }
        else if (arguments.Contains("-print-search-dirs"))
        {
            Console.WriteLine("libraries: =" + string.Join(Path.PathSeparator, Paths("Libraries")));
        }
        else if (arguments.FirstOrDefault(argument => argument.StartsWith("-print-file-name=", StringComparison.Ordinal)) is string file)
        {
            string name = file["-print-file-name=".Length..];
            Console.WriteLine(Value(name, name));
        }
        else if (arguments.Contains("-dM"))
        {
            Console.WriteLine("#define __x86_64__ 1");
        }
        else if (arguments.Contains("-v"))
        {
            Console.Error.WriteLine("#include <...> search starts here:");
            foreach (string path in Paths(arguments.Contains("c++") ? "CppIncludes" : "CIncludes"))
            {
                Console.Error.WriteLine(" " + path);
            }

            Console.Error.WriteLine("End of search list.");
        }
        else if (arguments.FirstOrDefault(argument => argument.Contains("print-prog-name=", StringComparison.Ordinal)) is string program)
        {
            Console.WriteLine(program[(program.IndexOf('=') + 1)..]);
        }

        return 0;
    }

    private static bool ClaimFirstIdentity(string configurationPath)
    {
        string marker = configurationPath + ".blocked";
        try
        {
            using FileStream stream = File.Open(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (IOException) when (File.Exists(marker))
        {
            return false;
        }
    }
}
