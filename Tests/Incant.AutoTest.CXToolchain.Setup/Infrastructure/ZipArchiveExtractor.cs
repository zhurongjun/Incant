using System.IO.Compression;
using System.Text;

namespace Incant.AutoTest.CXToolchain.Setup;

internal static class ZipArchiveExtractor
{
    private const int FileTypeMask = 0xF000;
    private const int DirectoryType = 0x4000;
    private const int RegularFileType = 0x8000;
    private const int SymbolicLinkType = 0xA000;
    private const int PermissionMask = 0x01FF;
    private const long MaximumLinkTargetLength = 16 * 1024;

    internal static async Task ExtractAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        Directory.CreateDirectory(root);
        await using FileStream archiveStream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        ZipEntry[] entries = archive.Entries
            .Select(entry => DescribeEntry(root, entry))
            .ToArray();
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        string? duplicatePath = entries
            .GroupBy(entry => entry.DestinationPath, comparer)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        if (duplicatePath is not null)
        {
            throw new InvalidDataException(
                $"ZIP archive contains multiple entries for '{duplicatePath}'.");
        }

        HashSet<string> linkPaths = entries
            .Where(entry => entry.Kind == ZipEntryKind.SymbolicLink)
            .Select(entry => entry.DestinationPath)
            .ToHashSet(comparer);
        foreach (ZipEntry entry in entries.Where(
            entry => entry.Kind != ZipEntryKind.SymbolicLink))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoLinkParent(root, entry.DestinationPath, linkPaths);
            if (entry.Kind == ZipEntryKind.Directory)
            {
                Directory.CreateDirectory(entry.DestinationPath);
                continue;
            }

            await ExtractFileAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        foreach (ZipEntry entry in entries.Where(
            entry => entry.Kind == ZipEntryKind.SymbolicLink))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoLinkParent(root, entry.DestinationPath, linkPaths);
            await CreateSymbolicLinkAsync(root, entry, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static ZipEntry DescribeEntry(string root, ZipArchiveEntry entry)
    {
        string destinationPath = ResolveArchivePath(root, entry.FullName);
        int unixMode = (int)(unchecked((uint)entry.ExternalAttributes) >> 16);
        int fileType = unixMode & FileTypeMask;
        ZipEntryKind kind;
        if (fileType == SymbolicLinkType)
        {
            kind = ZipEntryKind.SymbolicLink;
        }
        else if (fileType == DirectoryType
            || (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0
            || entry.FullName.EndsWith('/')
            || entry.FullName.EndsWith('\\'))
        {
            kind = ZipEntryKind.Directory;
        }
        else if (fileType is 0 or RegularFileType)
        {
            kind = ZipEntryKind.File;
        }
        else
        {
            throw new InvalidDataException(
                $"ZIP entry '{entry.FullName}' has unsupported Unix file type "
                + $"0x{fileType:X4}.");
        }

        return new ZipEntry(entry, destinationPath, kind, unixMode);
    }

    private static async Task ExtractFileAsync(
        ZipEntry entry,
        CancellationToken cancellationToken)
    {
        string? parent = Path.GetDirectoryName(entry.DestinationPath);
        if (parent is null)
        {
            throw new InvalidDataException(
                $"ZIP entry '{entry.Entry.FullName}' has no destination directory.");
        }

        Directory.CreateDirectory(parent);
        await using Stream source = entry.Entry.Open();
        await using (var destination = new FileStream(
            entry.DestinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        if (!OperatingSystem.IsWindows() && (entry.UnixMode & PermissionMask) != 0)
        {
            File.SetUnixFileMode(
                entry.DestinationPath,
                (UnixFileMode)(entry.UnixMode & PermissionMask));
        }
    }

    private static async Task CreateSymbolicLinkAsync(
        string root,
        ZipEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Entry.Length is <= 0 or > MaximumLinkTargetLength)
        {
            throw new InvalidDataException(
                $"ZIP symbolic link '{entry.Entry.FullName}' has an invalid target length.");
        }

        string target;
        await using (Stream stream = entry.Entry.Open())
        using (var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true))
        {
            target = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        if (target.Length == 0 || target.Contains('\0'))
        {
            throw new InvalidDataException(
                $"ZIP symbolic link '{entry.Entry.FullName}' has an invalid target.");
        }

        string normalizedTarget = target
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedTarget))
        {
            throw new InvalidDataException(
                $"ZIP symbolic link '{entry.Entry.FullName}' has absolute target '{target}'.");
        }

        string? parent = Path.GetDirectoryName(entry.DestinationPath);
        if (parent is null)
        {
            throw new InvalidDataException(
                $"ZIP symbolic link '{entry.Entry.FullName}' has no destination directory.");
        }

        string resolvedTarget = Path.GetFullPath(normalizedTarget, parent);
        if (!IsWithin(root, resolvedTarget))
        {
            throw new InvalidDataException(
                $"ZIP symbolic link '{entry.Entry.FullName}' target '{target}' escapes "
                + "the extraction root.");
        }

        Directory.CreateDirectory(parent);
        if (File.Exists(entry.DestinationPath) || Directory.Exists(entry.DestinationPath))
        {
            throw new InvalidDataException(
                $"ZIP symbolic link destination '{entry.DestinationPath}' already exists.");
        }

        try
        {
            if (OperatingSystem.IsWindows() && Directory.Exists(resolvedTarget))
            {
                Directory.CreateSymbolicLink(entry.DestinationPath, target);
            }
            else
            {
                File.CreateSymbolicLink(entry.DestinationPath, target);
            }
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            throw new InvalidDataException(
                $"ZIP symbolic link '{entry.Entry.FullName}' to '{target}' could not be "
                + "created safely.",
                exception);
        }
    }

    private static string ResolveArchivePath(string root, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName.Contains('\0'))
        {
            throw new InvalidDataException("ZIP archive contains an invalid empty entry name.");
        }

        string relative = entryName
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative))
        {
            throw new InvalidDataException(
                $"ZIP entry '{entryName}' has an absolute path.");
        }

        string destinationPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(relative, root));
        if (!IsWithin(root, destinationPath)
            || string.Equals(
                root,
                destinationPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"ZIP entry '{entryName}' escapes or replaces the extraction root.");
        }

        return destinationPath;
    }

    private static void EnsureNoLinkParent(
        string root,
        string path,
        IReadOnlySet<string> linkPaths)
    {
        string? current = Path.GetDirectoryName(path);
        while (current is not null && !PathEquals(current, root))
        {
            if (linkPaths.Contains(current))
            {
                throw new InvalidDataException(
                    $"ZIP entry '{path}' traverses symbolic link entry '{current}'.");
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static bool IsWithin(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative == "."
            || !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            && !relative.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private enum ZipEntryKind
    {
        File,
        Directory,
        SymbolicLink,
    }

    private sealed record ZipEntry(
        ZipArchiveEntry Entry,
        string DestinationPath,
        ZipEntryKind Kind,
        int UnixMode);
}
