namespace Incant.AutoTest.CppToolchain.Setup;

internal sealed class SetupPathGuard
{
    internal SetupPathGuard(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string? volumeRoot = Path.GetPathRoot(RootPath);
        if (volumeRoot is not null && PathEquals(RootPath, volumeRoot))
        {
            throw new ArgumentException(
                "The toolchain root cannot be a filesystem root.",
                nameof(rootPath));
        }
    }

    internal string RootPath { get; }

    internal string DownloadsRoot => AssertChild(Path.Combine(RootPath, "downloads"));

    internal string AssertChild(string path)
    {
        string resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string relative = Path.GetRelativePath(RootPath, resolved);
        if (relative.Length == 0
            || relative == "."
            || Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to modify '{resolved}' because it is not below '{RootPath}'.");
        }

        EnsureNoRedirectedChild(resolved);
        return resolved;
    }

    internal void ResetDirectory(string path)
    {
        string resolved = AssertChild(path);
        if (File.Exists(resolved))
        {
            DeleteFile(resolved);
        }

        DeleteDirectory(resolved);
        Directory.CreateDirectory(resolved);
    }

    internal void DeleteDirectory(string path)
    {
        string resolved = AssertChild(path);
        if (!Directory.Exists(resolved))
        {
            return;
        }

        try
        {
            Directory.Delete(resolved, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            ClearReadOnlyAttributes(resolved);
            Directory.Delete(resolved, recursive: true);
        }
    }

    internal void DeleteFile(string path)
    {
        string resolved = AssertChild(path);
        if (File.Exists(resolved))
        {
            File.SetAttributes(resolved, FileAttributes.Normal);
            File.Delete(resolved);
        }
    }

    internal void ReplaceDirectory(string preparedPath, string destinationPath)
    {
        string prepared = AssertChild(preparedPath);
        string destination = AssertChild(destinationPath);
        string backup = AssertChild(destination + $".replaced.{Guid.NewGuid():N}");

        if (!Directory.Exists(prepared))
        {
            throw new DirectoryNotFoundException(
                $"Prepared installation directory '{prepared}' does not exist.");
        }

        if (File.Exists(destination))
        {
            DeleteFile(destination);
        }

        bool movedExisting = false;
        if (Directory.Exists(destination))
        {
            Directory.Move(destination, backup);
            movedExisting = true;
        }

        try
        {
            Directory.Move(prepared, destination);
        }
        catch (Exception publishError)
        {
            if (movedExisting
                && !Directory.Exists(destination)
                && !File.Exists(destination)
                && Directory.Exists(backup))
            {
                try
                {
                    Directory.Move(backup, destination);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        $"Could not publish '{prepared}' or restore '{destination}'.",
                        publishError,
                        rollbackError);
                }
            }

            throw;
        }

        if (movedExisting && Directory.Exists(backup))
        {
            DeleteDirectory(backup);
        }
    }

    internal static string RequireFile(string path, string description)
    {
        string resolved = Path.GetFullPath(path);
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException(
                $"{description} was not found at '{resolved}'.",
                resolved);
        }

        return ResolveLink(resolved);
    }

    internal static string RequireDirectory(string path, string description)
    {
        string resolved = Path.GetFullPath(path);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException(
                $"{description} was not found at '{resolved}'.");
        }

        return ResolveLink(resolved);
    }

    internal static string ResolveLink(string path)
    {
        string resolved = Path.GetFullPath(path);
        FileSystemInfo information = Directory.Exists(resolved)
            ? new DirectoryInfo(resolved)
            : new FileInfo(resolved);
        try
        {
            return information.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? resolved;
        }
        catch (IOException)
        {
            return resolved;
        }
        catch (UnauthorizedAccessException)
        {
            return resolved;
        }
        catch (NotSupportedException)
        {
            return resolved;
        }
    }

    private void EnsureNoRedirectedChild(string path)
    {
        string? current = path;
        while (current is not null && !PathEquals(current, RootPath))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    $"Refusing to modify '{path}' because '{current}' redirects outside "
                    + "the managed toolchain tree.");
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (string file in Directory.EnumerateFiles(
            root,
            "*",
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            }))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
