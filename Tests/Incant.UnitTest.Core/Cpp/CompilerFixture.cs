using System.Text.Json;

namespace Incant.UnitTest.Core.Cpp;

internal sealed class CompilerFixture : IDisposable
{
    internal CompilerFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "Incant.UnitTest.Core", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    internal string Root { get; }

    internal string DirectoryPath(string relative)
    {
        string path = Path.GetFullPath(Path.Combine(Root, relative));
        Directory.CreateDirectory(path);
        return path;
    }

    internal string FilePath(string relative, string content = "")
    {
        string path = Path.GetFullPath(Path.Combine(Root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    internal string Compiler(string relative, IReadOnlyDictionary<string, object>? values = null)
    {
        string path = Path.GetFullPath(Path.Combine(Root, relative + (OperatingSystem.IsWindows() ? ".exe" : "")));
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
        string helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..",
            "Incant.ProcessTestHelper", configuration));
        foreach (string file in Directory.EnumerateFiles(helper))
        {
            if (Path.GetExtension(file) is ".dll" or ".json")
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
            }
        }

        File.Copy(Path.Combine(helper, "Incant.ProcessTestHelper" + (OperatingSystem.IsWindows() ? ".exe" : "")), path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Configure(path, values ?? new Dictionary<string, object>());
        return path;
    }

    internal static void Configure(string path, IReadOnlyDictionary<string, object> values) =>
        File.WriteAllText(path + ".compiler.json", JsonSerializer.Serialize(values));

    internal static IReadOnlyDictionary<string, string?> Environment() => new Dictionary<string, string?>();

    public void Dispose()
    {
        Directory.Delete(Root, recursive: true);
    }
}
