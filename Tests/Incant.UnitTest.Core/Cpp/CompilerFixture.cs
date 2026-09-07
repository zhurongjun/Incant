using System.Text;
using System.Text.Json;
using Incant.TestSupport;

namespace Incant.UnitTest.Core.Cpp;

internal sealed class CompilerFixture : IDisposable
{
    private readonly TestDirectory _directory;

    private readonly List<string> _compilers = [];

    internal CompilerFixture(string? parent = null)
    {
        _directory = new TestDirectory(parent);
    }

    internal string Root => _directory.Root;

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
        _ = CompilerTestHost.CreateEnvironment();
        CompilerTestHost.CopyTo(path);
        Configure(path, values ?? new Dictionary<string, object>());
        _compilers.Add(path);
        return path;
    }

    internal static void Configure(string path, IReadOnlyDictionary<string, object> values) =>
        File.WriteAllText(path + ".compiler.json", JsonSerializer.Serialize(values));

    internal static IReadOnlyList<CompilerInvocation> Invocations(string compiler) =>
        CompilerInvocationStore.Read(compiler + ".compiler.json.invocations");

    internal static IReadOnlyDictionary<string, string?> Environment() => CompilerTestHost.CreateEnvironment();

    public void Dispose()
    {
        foreach (string compiler in _compilers)
        {
            string directory = compiler + ".compiler.json.invocations";
            var evidence = new StringBuilder();
            evidence.AppendLine(compiler);
            try
            {
                if (Directory.Exists(directory))
                {
                    foreach (string file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
                    {
                        evidence.AppendLine(Path.GetFileName(file)).AppendLine(File.ReadAllText(file));
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                evidence.AppendLine($"Could not read invocation evidence: {exception.Message}");
            }

            TestContext.Current.AddAttachment($"compiler-{Path.GetFileName(Root)}-{Path.GetRelativePath(Root, compiler)}",
                evidence.ToString());
        }

        _directory.Dispose();
    }
}
