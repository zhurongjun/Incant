namespace Incant.UnitTest.CX;

/// <summary>Allocates fixtures below a physical temporary root; links inside the fixture remain test inputs.</summary>
internal sealed class TestDirectory : IDisposable
{
    internal TestDirectory(string? parent = null)
    {
        string physicalParent = ResolveParent(new DirectoryInfo(parent ?? Path.GetTempPath()), 40);
        Root = Path.Combine(physicalParent, "Incant.UnitTest.CX", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    internal string Root { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup observations must not replace a failure from the test body.
            TestContext.Current.AddWarning($"Could not remove test fixture '{Root}': {exception.Message}");
        }
    }

    private static string ResolveParent(DirectoryInfo directory, int remainingLinks)
    {
        if (remainingLinks == 0)
        {
            throw new IOException($"The test directory has too many ancestor links: '{directory.FullName}'.");
        }

        if (directory.Parent is not DirectoryInfo parent)
        {
            return directory.FullName;
        }

        string physicalParent = ResolveParent(parent, remainingLinks);
        var entry = new DirectoryInfo(Path.Combine(physicalParent, directory.Name));
        FileSystemInfo? target = entry.ResolveLinkTarget(returnFinalTarget: true);
        return target is null
            ? entry.FullName
            : ResolveParent(new DirectoryInfo(target.FullName), remainingLinks - 1);
    }
}
