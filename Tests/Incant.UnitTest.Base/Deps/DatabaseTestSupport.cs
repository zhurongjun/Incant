using Incant.Base.Deps;

namespace Incant.UnitTest.Base.Deps;

internal sealed class DatabaseTestDirectory : IDisposable
{
    internal DatabaseTestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Incant.UnitTest.Base", "Deps", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    internal string DatabasePath => System.IO.Path.Combine(Path, "records.db");

    internal string CreateFile(string name, string content = "contents")
    {
        string path = System.IO.Path.Combine(Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    internal Database Create(bool readOnly = false, FileSnapshotCache? cache = null) =>
        new(DatabasePath, cache, readOnly: readOnly);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
