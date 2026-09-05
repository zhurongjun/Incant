using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Incant.Base.Deps;

/// <summary>
/// Describes files discovered or produced while refreshing a dependency.
/// </summary>
/// <remarks>
/// Files added through <see cref="AddExternalFile"/> or <see cref="AddExternalFileRange"/> inside a dependency action
/// are captured before the record is saved.
/// </remarks>
public struct Record
{
    /// <summary>
    /// Initializes an empty dependency record.
    /// </summary>
    public Record()
    {
    }

    /// <summary>
    /// Adds a file that must continue to exist and remain unchanged for the record to stay current.
    /// </summary>
    /// <param name="path">The external file path.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public void AddExternalFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            throw new ArgumentException("The external file path cannot be empty.", nameof(path));
        }

        ExternalFiles.Add(new FileSnapshot(path, DateTime.MinValue, null));
    }

    /// <summary>
    /// Adds files that must continue to exist and remain unchanged for the record to stay current.
    /// </summary>
    /// <param name="paths">The external file paths.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="paths"/> or one of its elements is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">One of the paths is empty.</exception>
    public void AddExternalFileRange(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (string path in paths)
        {
            AddExternalFile(path);
        }
    }

    internal string Key { get; init; } = string.Empty;

    internal bool UseSHA { get; init; }

    internal List<string> Args { get; init; } = [];

    internal List<FileSnapshot> Files { get; init; } = [];

    internal List<FileSnapshot> ExternalFiles { get; init; } = [];
}

/// <summary>
/// Controls how a dependency record is validated.
/// </summary>
public struct CheckOptions
{
    /// <summary>
    /// Gets a value indicating whether file contents are compared with SHA-256 instead of UTC modification times.
    /// </summary>
    public bool UseSHA { get; init; }

    /// <summary>
    /// Gets a value indicating whether the dependency action must run regardless of the saved record.
    /// </summary>
    public bool Force { get; init; }
}

/// <summary>
/// Caches file metadata shared by one or more dependency databases.
/// </summary>
/// <remarks>
/// The cache represents a caller-controlled view of the file system. Call <see cref="Clear"/> after files may have
/// changed outside dependency actions.
/// </remarks>
public sealed class FileSnapshotCache
{
    /// <summary>
    /// Removes all cached file metadata.
    /// </summary>
    public void Clear() => _files.Clear();

    // A null snapshot records that the path was observed as missing.
    internal readonly ConcurrentDictionary<string, FileSnapshot?> _files =
        new(StringComparer.Ordinal);
}

/// <summary>An immutable dependency file snapshot.</summary>
/// <param name="Path">The file path.</param>
/// <param name="LastWriteTimeUtc">The UTC modification time, or MinValue for a missing file.</param>
/// <param name="Sha256">The optional content digest.</param>
public readonly record struct FileSnapshot(
    string Path,
    DateTime LastWriteTimeUtc,
    Sha256Digest? Sha256);

/// <summary>A fixed-size SHA-256 digest with value equality.</summary>
/// <param name="Part0">The first big-endian 64-bit part.</param>
/// <param name="Part1">The second big-endian 64-bit part.</param>
/// <param name="Part2">The third big-endian 64-bit part.</param>
/// <param name="Part3">The fourth big-endian 64-bit part.</param>
public readonly record struct Sha256Digest(
    ulong Part0,
    ulong Part1,
    ulong Part2,
    ulong Part3)
{
    /// <summary>Creates a digest from exactly 32 bytes in hash output order.</summary>
    /// <exception cref="ArgumentException">The input length is not 32 bytes.</exception>
    public static Sha256Digest FromBytes(ReadOnlySpan<byte> value)
    {
        if (value.Length != SizeInBytes)
        {
            throw new ArgumentException("A SHA-256 digest must contain exactly 32 bytes.", nameof(value));
        }

        return new Sha256Digest(
            BinaryPrimitives.ReadUInt64BigEndian(value[..8]),
            BinaryPrimitives.ReadUInt64BigEndian(value.Slice(8, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(value.Slice(16, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(value.Slice(24, 8)));
    }

    /// <summary>Parses exactly 64 hexadecimal characters.</summary>
    /// <exception cref="FormatException">The input is not a SHA-256 hexadecimal digest.</exception>
    public static Sha256Digest Parse(string value)
    {
        if (value.Length != HexCharacterCount)
        {
            throw new FormatException("The dependency record contains an invalid SHA-256 digest.");
        }

        return FromBytes(Convert.FromHexString(value));
    }

    /// <summary>Formats the digest as 64 lowercase hexadecimal characters.</summary>
    public override string ToString()
    {
        Span<byte> value = stackalloc byte[SizeInBytes];
        CopyTo(value);
        return Convert.ToHexStringLower(value);
    }

    /// <summary>The number of bytes in a SHA-256 digest.</summary>
    public const int SizeInBytes = 32;

    /// <summary>Copies the digest to the first 32 destination bytes in hash output order.</summary>
    /// <exception cref="ArgumentException">The destination has fewer than 32 bytes.</exception>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < SizeInBytes)
        {
            throw new ArgumentException("The destination must contain at least 32 bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, Part0);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8, 8), Part1);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(16, 8), Part2);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(24, 8), Part3);
    }

    private const int HexCharacterCount = SizeInBytes * 2;
}

/// <summary>Normalizes dependency records and captures or compares their file metadata.</summary>
internal static class RecordUtils
{
    internal static Record CreateRecord(
        string key, IEnumerable<string>? files, IEnumerable<string>? args, bool useSHA) =>
        new()
        {
            Key = CanonicalizeKey(key),
            Files = CollectInputFiles(files),
            Args = CollectInputArgs(args),
            UseSHA = useSHA
        };

    internal static bool IsOutdated(Record input, Record? saved, FileSnapshotCache? cache)
    {
        if (saved is not Record record)
        {
            return true;
        }

        if (record.UseSHA != input.UseSHA
            || record.Files.Count != input.Files.Count
            || !record.Args.SequenceEqual(input.Args, StringComparer.Ordinal))
        {
            return true;
        }

        for (int index = 0; index < record.Files.Count; index++)
        {
            FileSnapshot expected = record.Files[index];
            if (!string.Equals(input.Files[index].Path, expected.Path, StringComparison.Ordinal)
                || IsFileOutdated(expected, record.UseSHA, cache))
            {
                return true;
            }
        }

        foreach (FileSnapshot expected in record.ExternalFiles)
        {
            if (IsFileOutdated(expected, record.UseSHA, cache))
            {
                return true;
            }
        }

        return false;
    }

    internal static void CaptureFiles(List<FileSnapshot> files, bool useSHA, FileSnapshotCache? cache)
    {
        for (int index = 0; index < files.Count; index++)
        {
            string path = files[index].Path;
            files[index] = GetFileSnapshot(path, useSHA, cache, refresh: true)
                ?? new FileSnapshot(path, DateTime.MinValue, null);
        }
    }

    internal static string CanonicalizeKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("The key cannot be empty.", nameof(key));
        }

        if (key is "." or "..")
        {
            throw new ArgumentException("The key must be a file name rather than a relative path.", nameof(key));
        }

        if (key[^1] is '.' or ' ')
        {
            throw new ArgumentException("The key cannot end with a period or space.", nameof(key));
        }

        foreach (char character in key)
        {
            if (char.IsControl(character) || PortableInvalidFileNameCharacters.Contains(character))
            {
                throw new ArgumentException(
                    $"The key contains an invalid file name character: '{character}'.",
                    nameof(key));
            }
        }

        int extensionIndex = key.IndexOf('.');
        string baseName = extensionIndex < 0 ? key : key[..extensionIndex];
        if (s_reservedFileNames.Contains(baseName))
        {
            throw new ArgumentException("The key uses a reserved file name.", nameof(key));
        }

        return key.ToLowerInvariant();
    }

    private static bool IsFileOutdated(FileSnapshot expected, bool useSHA, FileSnapshotCache? cache)
    {
        FileSnapshot? current = GetFileSnapshot(expected.Path, useSHA, cache, refresh: false);
        return current is not FileSnapshot snapshot || (useSHA
            ? snapshot.Sha256 != expected.Sha256
            : snapshot.LastWriteTimeUtc != expected.LastWriteTimeUtc);
    }

    private static FileSnapshot? GetFileSnapshot(string path, bool useSHA, FileSnapshotCache? cache, bool refresh)
    {
        if (!refresh && cache is not null
            && cache._files.TryGetValue(path, out FileSnapshot? cached)
            && (cached is null || !useSHA || cached.Value.Sha256 is not null))
        {
            return cached;
        }

        FileSnapshot? snapshot = null;
        try
        {
            if (File.Exists(path))
            {
                snapshot = new FileSnapshot(path, File.GetLastWriteTimeUtc(path),
                    useSHA ? CalculateSha256(path) : null);
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // An input may disappear between the existence check and opening it for hashing.
        }

        if (cache is not null)
        {
            cache._files[path] = snapshot;
        }

        return snapshot;
    }

    private static Sha256Digest CalculateSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> hash = stackalloc byte[Sha256Digest.SizeInBytes];
        int bytesWritten = SHA256.HashData(stream, hash);
        Debug.Assert(bytesWritten == Sha256Digest.SizeInBytes);
        return Sha256Digest.FromBytes(hash);
    }

    private static List<FileSnapshot> CollectInputFiles(IEnumerable<string>? files)
    {
        if (files is null)
        {
            return [];
        }

        var fileSet = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string? file in files)
        {
            if (string.IsNullOrEmpty(file))
            {
                throw new ArgumentException("Input file paths cannot be null or empty.", nameof(files));
            }

            fileSet.Add(file);
        }

        return [.. fileSet.Select(static path => new FileSnapshot(path, DateTime.MinValue, null))];
    }

    private static List<string> CollectInputArgs(IEnumerable<string>? args)
    {
        if (args is null)
        {
            return [];
        }

        var result = new List<string>();
        foreach (string? argument in args)
        {
            if (argument is null)
            {
                throw new ArgumentException("Input arguments cannot contain null values.", nameof(args));
            }

            result.Add(argument);
        }

        return result;
    }

    private const string PortableInvalidFileNameCharacters = "<>:\"/\\|?*";

    private static readonly HashSet<string> s_reservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };
}
