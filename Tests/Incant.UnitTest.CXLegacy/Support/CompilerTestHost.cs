using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace Incant.UnitTest.CXLegacy;

internal static class CompilerTestHost
{
    internal static IReadOnlyDictionary<string, string?> CreateEnvironment()
    {
        var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        DirectoryInfo? dotnetRoot = runtimeDirectory.Parent?.Parent?.Parent;
        if (dotnetRoot is null || !Directory.Exists(Path.Combine(dotnetRoot.FullName, "host", "fxr")))
        {
            throw new InvalidOperationException(
                $"The current runtime '{runtimeDirectory.FullName}' has no apphost-compatible .NET installation.");
        }

        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
        return new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>
        {
            ["DOTNET_ROOT"] = dotnetRoot.FullName,
            [$"DOTNET_ROOT_{architecture}"] = dotnetRoot.FullName,
        });
    }

    internal static void CopyTo(string invocationPath)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
        string helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..",
            "Incant.ProcessTestHelper", configuration));
        string apphost = "Incant.ProcessTestHelper" + (OperatingSystem.IsWindows() ? ".exe" : "");
        foreach (string name in new[]
        {
            apphost,
            "Incant.ProcessTestHelper.dll",
            "Incant.ProcessTestHelper.deps.json",
            "Incant.ProcessTestHelper.runtimeconfig.json",
        })
        {
            string required = Path.Combine(helper, name);
            if (!File.Exists(required))
            {
                throw new FileNotFoundException("A required compiler test host file is missing. Build Incant.ProcessTestHelper first.", required);
            }
        }

        string directory = Path.GetDirectoryName(invocationPath)!;
        Directory.CreateDirectory(directory);
        foreach (string file in Directory.EnumerateFiles(helper))
        {
            if (Path.GetExtension(file) is ".dll" or ".json")
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
            }
        }

        File.Copy(Path.Combine(helper, apphost), invocationPath);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(invocationPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
