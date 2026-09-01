using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Incant.ProcessTestHelper;

internal static class Program
{
    private static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            return InvalidArgumentsExitCode;
        }

        switch (arguments[0])
        {
            case "arguments":
                Console.Out.Write(JsonSerializer.Serialize(arguments[1..]));
                return 0;

            case "streams":
                Console.Out.Write(arguments[1]);
                Console.Error.Write(arguments[2]);
                return int.Parse(arguments[3], CultureInfo.InvariantCulture);

            case "encoded-streams":
                WriteEncodedStream(Console.OpenStandardOutput(), arguments[1], arguments[2]);
                WriteEncodedStream(Console.OpenStandardError(), arguments[1], arguments[3]);
                return 0;

            case "environment":
                Console.Out.Write(JsonSerializer.Serialize(Environment.GetEnvironmentVariable(arguments[1])));
                return 0;

            case "working-directory":
                Console.Out.Write(Environment.CurrentDirectory);
                return 0;

            case "exit":
                return int.Parse(arguments[1], CultureInfo.InvariantCulture);

            case "touch":
                File.WriteAllText(arguments[1], string.Empty);
                return 0;

            case "wait":
                await WaitAsync(arguments[1], arguments[2]).ConfigureAwait(false);
                return 0;

            case "spawn-child":
                await SpawnChildAsync(arguments).ConfigureAwait(false);
                return 0;

            default:
                return InvalidArgumentsExitCode;
        }
    }

    private static void WriteEncodedStream(Stream stream, string encodingName, string value)
    {
        Encoding encoding = Encoding.GetEncoding(encodingName);
        byte[] content = encoding.GetBytes(value);
        stream.Write(content);
        stream.Flush();
    }

    private static async Task WaitAsync(string standardOutput, string standardError)
    {
        Console.Out.Write(standardOutput);
        Console.Out.Flush();
        Console.Error.Write(standardError);
        Console.Error.Flush();
        await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
    }

    private static async Task SpawnChildAsync(string[] arguments)
    {
        string helperAssemblyPath = Assembly.GetExecutingAssembly().Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = arguments[2],
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(arguments[3]);
        startInfo.ArgumentList.Add("--depsfile");
        startInfo.ArgumentList.Add(arguments[4]);
        startInfo.ArgumentList.Add(helperAssemblyPath);
        startInfo.ArgumentList.Add("wait");
        startInfo.ArgumentList.Add(string.Empty);
        startInfo.ArgumentList.Add(string.Empty);

        using Process child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The child test process could not be started.");
        File.WriteAllText(arguments[1], child.Id.ToString(CultureInfo.InvariantCulture));
        await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
    }

    private const int InvalidArgumentsExitCode = 2;
}
