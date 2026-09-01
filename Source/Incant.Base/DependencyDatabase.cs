using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Incant.Base;

/// <summary>
/// Tracks dependency actions and persists their records through a configurable backend.
/// </summary>
public class DependencyDatabase
{
    /// <summary>
    /// Creates a dependency database backed by loose CSV files.
    /// </summary>
    /// <param name="location">The parent directory that owns the database directory.</param>
    /// <param name="name">The portable file name used for the database directory.</param>
    /// <param name="cache">An optional file metadata cache shared by dependency databases.</param>
    /// <param name="defaultUseSHA">The default file comparison mode.</param>
    /// <returns>A dependency database that uses a <see cref="CsvDependencyDatabaseBackend"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="location"/> or <paramref name="name"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="location"/> is empty, or <paramref name="name"/> is not a portable file name.
    /// </exception>
    public static DependencyDatabase CreateCSV(
        string location,
        string name,
        DependencyDatabaseCache? cache = null,
        bool defaultUseSHA = false) =>
        new(new CsvDependencyDatabaseBackend(location, name), cache, defaultUseSHA);

    /// <summary>
    /// Initializes a dependency database with a custom storage backend.
    /// </summary>
    /// <param name="backend">The backend used to load, save, and clear dependency records.</param>
    /// <param name="cache">An optional file metadata cache shared by dependency databases.</param>
    /// <param name="defaultUseSHA">The default file comparison mode.</param>
    /// <exception cref="ArgumentNullException"><paramref name="backend"/> is <see langword="null"/>.</exception>
    public DependencyDatabase(
        IDependencyDatabaseBackend backend,
        DependencyDatabaseCache? cache = null,
        bool defaultUseSHA = false)
    {
        ArgumentNullException.ThrowIfNull(backend);

        _backend = backend;
        _cache = cache;
        _defaultUseSHA = defaultUseSHA;
    }

    /// <summary>
    /// Runs an action when its dependency record is missing or outdated.
    /// </summary>
    /// <param name="key">The dependency key used as the record file name.</param>
    /// <param name="action">The action that refreshes the dependency and reports external files.</param>
    /// <param name="files">The unordered set of input files.</param>
    /// <param name="args">The ordered input arguments.</param>
    /// <param name="options">Optional validation settings that replace the database defaults.</param>
    /// <returns><see langword="true"/> when the action ran; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="action"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is not a portable file name, an input file is null or empty, or an argument is null.
    /// </exception>
    public bool RunIfOutdated(
        string key,
        Action<DependencyRecord> action,
        IEnumerable<string>? files,
        IEnumerable<string>? args,
        DependencyCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        DependencyOperation operation = CreateOperation(key, files, args, options);
        if (!operation.Options.Force && !IsOutdated(operation))
        {
            return false;
        }

        DependencyRecord record = CreateRecord(operation);
        action(record);
        SaveCompletedRecord(record);
        return true;
    }

    /// <summary>
    /// Asynchronously runs an action when its dependency record is missing or outdated.
    /// </summary>
    /// <param name="key">The dependency key used as the record file name.</param>
    /// <param name="action">The asynchronous action that refreshes the dependency and reports external files.</param>
    /// <param name="files">The unordered set of input files.</param>
    /// <param name="args">The ordered input arguments.</param>
    /// <param name="options">Optional validation settings that replace the database defaults.</param>
    /// <returns>A task whose result is true when the action ran; otherwise, false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="action"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is not a portable file name, an input file is null or empty, or an argument is null.
    /// </exception>
    public async Task<bool> RunIfOutdated(
        string key,
        Func<DependencyRecord, Task> action,
        IEnumerable<string>? files,
        IEnumerable<string>? args,
        DependencyCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        DependencyOperation operation = CreateOperation(key, files, args, options);
        if (!operation.Options.Force && !IsOutdated(operation))
        {
            return false;
        }

        DependencyRecord record = CreateRecord(operation);
        await action(record).ConfigureAwait(false);
        SaveCompletedRecord(record);
        return true;
    }

    /// <summary>
    /// Clears every persisted record and the shared file metadata cache.
    /// </summary>
    /// <remarks>
    /// This method must not run concurrently with dependency actions.
    /// </remarks>
    public void ClearDatabase()
    {
        _backend.Clear();
        _cache?.Clear();
    }

    private DependencyOperation CreateOperation(
        string key,
        IEnumerable<string>? files,
        IEnumerable<string>? args,
        DependencyCheckOptions? options)
    {
        string canonicalKey = CanonicalizeKey(key);
        List<string> inputFiles = CollectInputFiles(files);
        List<string> inputArgs = CollectInputArgs(args);

        DependencyCheckOptions effectiveOptions = options ?? new DependencyCheckOptions
        {
            UseSHA = _defaultUseSHA
        };

        return new DependencyOperation(canonicalKey, inputFiles, inputArgs, effectiveOptions);
    }

    private bool IsOutdated(DependencyOperation operation)
    {
        DependencyRecord? loadedRecord = _backend.Load(operation.Key);
        if (loadedRecord is null)
        {
            return true;
        }

        DependencyRecord record = loadedRecord.Value;
        if (record.UseSHA != operation.Options.UseSHA
            || record.Files.Count != operation.Files.Count
            || !record.Args.SequenceEqual(operation.Args, StringComparer.Ordinal))
        {
            return true;
        }

        for (int index = 0; index < record.Files.Count; index++)
        {
            DependencyFileSnapshot expectedSnapshot = record.Files[index];
            if (!string.Equals(operation.Files[index], expectedSnapshot.Path, StringComparison.Ordinal)
                || IsFileOutdated(expectedSnapshot, record.UseSHA))
            {
                return true;
            }
        }

        foreach (DependencyFileSnapshot expectedSnapshot in record.ExternalFiles)
        {
            if (IsFileOutdated(expectedSnapshot, record.UseSHA))
            {
                return true;
            }
        }

        return false;
    }

    private DependencyRecord CreateRecord(DependencyOperation operation)
    {
        var fileSnapshots = new List<DependencyFileSnapshot>(operation.Files.Count);

        foreach (string path in operation.Files)
        {
            TryGetFileSnapshot(
                path,
                operation.Options.UseSHA,
                FileSnapshotMode.Refresh,
                out DependencyFileSnapshot snapshot);
            fileSnapshots.Add(snapshot);
        }

        return new DependencyRecord
        {
            Key = operation.Key,
            UseSHA = operation.Options.UseSHA,
            Args = operation.Args,
            Files = fileSnapshots
        };
    }

    private void SaveCompletedRecord(DependencyRecord record)
    {
        for (int index = 0; index < record.ExternalFiles.Count; index++)
        {
            string path = record.ExternalFiles[index].Path;
            TryGetFileSnapshot(
                path,
                record.UseSHA,
                FileSnapshotMode.Refresh,
                out DependencyFileSnapshot snapshot);
            record.ExternalFiles[index] = snapshot;
        }

        _backend.Save(record);
    }

    private bool IsFileOutdated(DependencyFileSnapshot expectedSnapshot, bool useSHA)
    {
        if (!TryGetFileSnapshot(
                expectedSnapshot.Path,
                useSHA,
                FileSnapshotMode.Cached,
                out DependencyFileSnapshot currentSnapshot))
        {
            return true;
        }

        return useSHA
            ? currentSnapshot.Sha256 != expectedSnapshot.Sha256
            : currentSnapshot.LastWriteTimeUtc != expectedSnapshot.LastWriteTimeUtc;
    }

    private bool TryGetFileSnapshot(
        string path,
        bool useSHA,
        FileSnapshotMode snapshotMode,
        out DependencyFileSnapshot snapshot)
    {
        if (_cache is not null
            && snapshotMode == FileSnapshotMode.Cached
            && _cache._files.TryGetValue(path, out DependencyFileSnapshot? cachedSnapshot))
        {
            if (cachedSnapshot is null)
            {
                snapshot = new DependencyFileSnapshot(path, DateTime.MinValue, null);
                return false;
            }

            if (!useSHA || cachedSnapshot.Value.Sha256 is not null)
            {
                snapshot = cachedSnapshot.Value;
                return true;
            }
        }

        if (!File.Exists(path))
        {
            snapshot = new DependencyFileSnapshot(path, DateTime.MinValue, null);
            CacheFileSnapshot(path, null);
            return false;
        }

        try
        {
            snapshot = new DependencyFileSnapshot(
                path,
                File.GetLastWriteTimeUtc(path),
                useSHA ? CalculateSha256(path) : null);
            CacheFileSnapshot(path, snapshot);
            return true;
        }
        catch (FileNotFoundException)
        {
            snapshot = new DependencyFileSnapshot(path, DateTime.MinValue, null);
            CacheFileSnapshot(path, null);
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            snapshot = new DependencyFileSnapshot(path, DateTime.MinValue, null);
            CacheFileSnapshot(path, null);
            return false;
        }
    }

    private static Sha256Digest CalculateSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> hash = stackalloc byte[Sha256Digest.SizeInBytes];
        int bytesWritten = SHA256.HashData(stream, hash);
        Debug.Assert(bytesWritten == Sha256Digest.SizeInBytes);
        return Sha256Digest.FromBytes(hash);
    }

    private void CacheFileSnapshot(string path, DependencyFileSnapshot? snapshot)
    {
        if (_cache is not null)
        {
            _cache._files[path] = snapshot;
        }
    }

    private static List<string> CollectInputFiles(IEnumerable<string>? files)
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

        return [.. fileSet];
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

    private static string CanonicalizeKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidatePortableFileName(key, nameof(key));
        return key.ToLowerInvariant();
    }

    internal static void ValidatePortableFileName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value cannot be empty.", parameterName);
        }

        if (value is "." or "..")
        {
            throw new ArgumentException("The value must be a file name rather than a relative path.", parameterName);
        }

        if (value[^1] is '.' or ' ')
        {
            throw new ArgumentException("The value cannot end with a period or space.", parameterName);
        }

        foreach (char character in value)
        {
            if (char.IsControl(character) || PortableInvalidFileNameCharacters.Contains(character))
            {
                throw new ArgumentException(
                    $"The value contains an invalid file name character: '{character}'.",
                    parameterName);
            }
        }

        int extensionIndex = value.IndexOf('.');
        string baseName = extensionIndex < 0 ? value : value[..extensionIndex];
        if (s_reservedFileNames.Contains(baseName))
        {
            throw new ArgumentException("The value uses a reserved file name.", parameterName);
        }
    }

    private sealed record DependencyOperation(
        string Key,
        List<string> Files,
        List<string> Args,
        DependencyCheckOptions Options);

    private enum FileSnapshotMode
    {
        Cached,
        Refresh
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

    private readonly IDependencyDatabaseBackend _backend;
    private readonly DependencyDatabaseCache? _cache;
    private readonly bool _defaultUseSHA;
}

/// <summary>
/// Describes files discovered or produced while refreshing a dependency.
/// </summary>
/// <remarks>
/// Files added through <see cref="AddExternalFile"/> or <see cref="AddExternalFileRange"/> inside a dependency action
/// are captured before the record is saved.
/// </remarks>
public struct DependencyRecord
{
    /// <summary>
    /// Initializes an empty dependency record.
    /// </summary>
    public DependencyRecord()
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

        ExternalFiles.Add(new DependencyFileSnapshot(path, DateTime.MinValue, null));
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
    internal List<DependencyFileSnapshot> Files { get; init; } = [];
    internal List<DependencyFileSnapshot> ExternalFiles { get; init; } = [];
}

/// <summary>
/// Controls how a dependency record is validated.
/// </summary>
public struct DependencyCheckOptions
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
public sealed class DependencyDatabaseCache
{
    /// <summary>
    /// Removes all cached file metadata.
    /// </summary>
    public void Clear() => _files.Clear();

    // A null snapshot records that the path was observed as missing.
    internal readonly ConcurrentDictionary<string, DependencyFileSnapshot?> _files =
        new(StringComparer.Ordinal);
}

/// <summary>
/// Loads and stores dependency records without exposing their physical representation.
/// </summary>
/// <remarks>
/// Implementations must support concurrent calls to <see cref="Load"/> and <see cref="Save"/>. The caller must not
/// invoke <see cref="Clear"/> concurrently with either operation.
/// </remarks>
public interface IDependencyDatabaseBackend
{
    /// <summary>
    /// Loads a dependency record.
    /// </summary>
    /// <param name="key">The canonical dependency key.</param>
    /// <returns>The record, or <see langword="null"/> when it does not exist or cannot be decoded.</returns>
    public DependencyRecord? Load(string key);

    /// <summary>
    /// Saves a complete dependency record.
    /// </summary>
    /// <param name="record">The dependency record to save.</param>
    public void Save(DependencyRecord record);

    /// <summary>
    /// Clears every stored dependency record.
    /// </summary>
    public void Clear();
}

/// <summary>
/// Stores each dependency record as a UTF-8 CSV file.
/// </summary>
public sealed class CsvDependencyDatabaseBackend : IDependencyDatabaseBackend
{
    /// <summary>
    /// Initializes a loose-file CSV backend.
    /// </summary>
    /// <param name="location">The parent directory that owns the database directory.</param>
    /// <param name="name">The portable file name used for the database directory.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="location"/> or <paramref name="name"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="location"/> is empty, or <paramref name="name"/> is not a portable file name.
    /// </exception>
    public CsvDependencyDatabaseBackend(string location, string name)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(name);

        if (string.IsNullOrWhiteSpace(location))
        {
            throw new ArgumentException("The database location cannot be empty.", nameof(location));
        }

        DependencyDatabase.ValidatePortableFileName(name, nameof(name));
        _databaseDirectoryPath = Path.Combine(Path.GetFullPath(location), name + DatabaseDirectorySuffix);
        Directory.CreateDirectory(_databaseDirectoryPath);
    }

    /// <inheritdoc />
    public DependencyRecord? Load(string key)
    {
        string recordPath = GetRecordPath(key);
        if (!File.Exists(recordPath))
        {
            return null;
        }

        using RecordLockHandle recordLock = AcquireRecordLock(key);
        if (!File.Exists(recordPath))
        {
            return null;
        }

        try
        {
            string content = File.ReadAllText(recordPath, s_utf8Encoding);
            return DeserializeRecord(content, key);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Save(DependencyRecord record)
    {
        string recordPath = GetRecordPath(record.Key);
        string content = SerializeRecord(record);

        Directory.CreateDirectory(_databaseDirectoryPath);
        using RecordLockHandle recordLock = AcquireRecordLock(record.Key);
        File.WriteAllText(recordPath, content, s_utf8Encoding);
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (Directory.Exists(_databaseDirectoryPath))
        {
            Directory.Delete(_databaseDirectoryPath, recursive: true);
        }

        Directory.CreateDirectory(_databaseDirectoryPath);
    }

    private string GetRecordPath(string key) =>
        GetDatabaseFilePath(key, RecordFileExtension);

    private string GetDatabaseFilePath(string key, string extension)
    {
        ArgumentNullException.ThrowIfNull(key);
        DependencyDatabase.ValidatePortableFileName(key, nameof(key));
        return Path.Combine(_databaseDirectoryPath, key + extension);
    }

    private RecordLockHandle AcquireRecordLock(string key)
    {
        string lockPath = GetDatabaseFilePath(key, RecordLockExtension);
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return new RecordLockHandle(lockPath, stream);
            }
            catch (IOException) when (stopwatch.ElapsedMilliseconds < LockAcquireTimeoutMilliseconds)
            {
                Thread.Sleep(LockAcquireRetryDelayMilliseconds);
            }
            catch (UnauthorizedAccessException) when (
                stopwatch.ElapsedMilliseconds < LockAcquireTimeoutMilliseconds)
            {
                Thread.Sleep(LockAcquireRetryDelayMilliseconds);
            }
        }
    }

    private static string SerializeRecord(DependencyRecord record)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, "key", record.Key);
        AppendCsvRow(builder, "mode", record.UseSHA ? ShaMode : TimestampMode);

        foreach (string argument in record.Args)
        {
            AppendCsvRow(builder, "input_arg", argument);
        }

        foreach (DependencyFileSnapshot file in record.Files)
        {
            AppendCsvRow(
                builder,
                "input_file",
                file.Path,
                SerializeDateTime(file.LastWriteTimeUtc),
                SerializeSha256(file.Sha256, record.UseSHA));
        }

        foreach (DependencyFileSnapshot file in record.ExternalFiles)
        {
            AppendCsvRow(
                builder,
                "external_file",
                file.Path,
                SerializeDateTime(file.LastWriteTimeUtc),
                SerializeSha256(file.Sha256, record.UseSHA));
        }

        return builder.ToString();
    }

    private static DependencyRecord DeserializeRecord(string content, string expectedKey)
    {
        List<List<string>> rows = ParseCsv(content);
        string? key = null;
        bool? useSHA = null;
        var args = new List<string>();
        var files = new List<DependencyFileSnapshot>();
        var externalFiles = new List<DependencyFileSnapshot>();

        foreach (List<string> row in rows)
        {
            if (row.Count == 1 && row[0].Length == 0)
            {
                continue;
            }

            switch (row[0])
            {
                case "key":
                    if (key is not null || row.Count != 2)
                    {
                        throw new FormatException("The dependency record has an invalid key row.");
                    }

                    key = row[1];
                    break;

                case "mode":
                    if (useSHA is not null || row.Count != 2)
                    {
                        throw new FormatException("The dependency record has an invalid mode row.");
                    }

                    useSHA = row[1] switch
                    {
                        ShaMode => true,
                        TimestampMode => false,
                        _ => throw new FormatException("The dependency record has an unsupported validation mode.")
                    };
                    break;

                case "input_arg":
                    EnsureFieldCount(row, 2, "input_arg");
                    args.Add(row[1]);
                    break;

                case "input_file":
                    EnsureFieldCount(row, 4, "input_file");
                    files.Add(
                        new DependencyFileSnapshot(
                            row[1],
                            DeserializeDateTime(row[2]),
                            DeserializeSha256(row[3])));
                    break;

                case "external_file":
                    EnsureFieldCount(row, 4, "external_file");
                    externalFiles.Add(
                        new DependencyFileSnapshot(
                            row[1],
                            DeserializeDateTime(row[2]),
                            DeserializeSha256(row[3])));
                    break;

                default:
                    throw new FormatException($"The dependency record contains an unknown row '{row[0]}'.");
            }
        }

        if (key is null || useSHA is null)
        {
            throw new FormatException("The dependency record is missing required metadata.");
        }

        if (!string.Equals(key, expectedKey, StringComparison.Ordinal))
        {
            throw new FormatException("The dependency record key does not match its file name.");
        }

        if (!useSHA.Value
            && (files.Any(static file => file.Sha256 is not null)
                || externalFiles.Any(static file => file.Sha256 is not null)))
        {
            throw new FormatException("A timestamp dependency record cannot contain SHA-256 digests.");
        }

        return new DependencyRecord
        {
            Key = key,
            UseSHA = useSHA.Value,
            Args = args,
            Files = files,
            ExternalFiles = externalFiles
        };
    }

    private static List<List<string>> ParseCsv(string content)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool isQuoted = false;
        bool isAfterQuote = false;

        for (int index = 0; index < content.Length; index++)
        {
            char character = content[index];
            if (isQuoted)
            {
                if (character == '"')
                {
                    if (index + 1 < content.Length && content[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        isQuoted = false;
                        isAfterQuote = true;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            if (isAfterQuote)
            {
                if (character == ',')
                {
                    CompleteCsvField(row, field);
                    isAfterQuote = false;
                    continue;
                }

                if (character is '\r' or '\n')
                {
                    CompleteCsvRow(rows, ref row, field, content, ref index, character);
                    isAfterQuote = false;
                    continue;
                }

                throw new FormatException("A quoted CSV field contains trailing characters.");
            }

            switch (character)
            {
                case '"':
                    if (field.Length != 0)
                    {
                        throw new FormatException("A CSV quote must begin a field.");
                    }

                    isQuoted = true;
                    break;

                case ',':
                    CompleteCsvField(row, field);
                    break;

                case '\r':
                case '\n':
                    CompleteCsvRow(rows, ref row, field, content, ref index, character);
                    break;

                default:
                    field.Append(character);
                    break;
            }
        }

        if (isQuoted)
        {
            throw new FormatException("A quoted CSV field is not terminated.");
        }

        if (isAfterQuote || field.Length != 0 || row.Count != 0 || content.EndsWith(','))
        {
            CompleteCsvField(row, field);
            rows.Add(row);
        }

        return rows;
    }

    private static void CompleteCsvRow(
        List<List<string>> rows,
        ref List<string> row,
        StringBuilder field,
        string content,
        ref int index,
        char newline)
    {
        CompleteCsvField(row, field);
        rows.Add(row);
        row = [];

        if (newline == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
        {
            index++;
        }
    }

    private static void CompleteCsvField(List<string> row, StringBuilder field)
    {
        row.Add(field.ToString());
        field.Clear();
    }

    private static void AppendCsvRow(StringBuilder builder, params string[] fields)
    {
        for (int index = 0; index < fields.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            AppendCsvField(builder, fields[index]);
        }

        builder.AppendLine();
    }

    private static void AppendCsvField(StringBuilder builder, string value)
    {
        bool requiresQuotes = value.Contains(',')
            || value.Contains('"')
            || value.Contains('\r')
            || value.Contains('\n');
        if (!requiresQuotes)
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        builder.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
        builder.Append('"');
    }

    private static void EnsureFieldCount(List<string> row, int expectedCount, string rowName)
    {
        if (row.Count != expectedCount)
        {
            throw new FormatException($"The dependency record has an invalid {rowName} row.");
        }
    }

    private static string SerializeDateTime(DateTime value) =>
        value.ToBinary().ToString(CultureInfo.InvariantCulture);

    private static string SerializeSha256(Sha256Digest? digest, bool useSHA) =>
        useSHA && digest is not null ? digest.Value.ToString() : string.Empty;

    private static Sha256Digest? DeserializeSha256(string value) =>
        value.Length == 0 ? null : Sha256Digest.Parse(value);

    private static DateTime DeserializeDateTime(string value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long binaryValue))
        {
            throw new FormatException("The dependency record contains an invalid file time.");
        }

        try
        {
            return DateTime.FromBinary(binaryValue);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException("The dependency record contains an invalid file time.", exception);
        }
    }

    private static void TryDeleteFile(string path)
    {
        // Cleanup failures must not replace the result of the record operation.
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RecordLockHandle : IDisposable
    {
        public RecordLockHandle(string path, FileStream stream)
        {
            _path = path;
            _stream = stream;
        }

        public void Dispose()
        {
            _stream.Dispose();
            TryDeleteFile(_path);
        }

        private readonly string _path;
        private readonly FileStream _stream;
    }

    private const string DatabaseDirectorySuffix = ".db";
    private const string RecordFileExtension = ".csv";
    private const string RecordLockExtension = ".lock";
    private const string ShaMode = "sha256";
    private const string TimestampMode = "timestamp";
    private const int LockAcquireTimeoutMilliseconds = 5000;
    private const int LockAcquireRetryDelayMilliseconds = 10;

    private static readonly UTF8Encoding s_utf8Encoding = new(false, true);

    private readonly string _databaseDirectoryPath;
}

internal readonly record struct DependencyFileSnapshot(
    string Path,
    DateTime LastWriteTimeUtc,
    Sha256Digest? Sha256);

internal readonly record struct Sha256Digest(
    ulong Part0,
    ulong Part1,
    ulong Part2,
    ulong Part3)
{
    internal static Sha256Digest FromBytes(ReadOnlySpan<byte> value)
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

    internal static Sha256Digest Parse(string value)
    {
        if (value.Length != HexCharacterCount)
        {
            throw new FormatException("The dependency record contains an invalid SHA-256 digest.");
        }

        try
        {
            return FromBytes(Convert.FromHexString(value));
        }
        catch (FormatException exception)
        {
            throw new FormatException(
                "The dependency record contains an invalid SHA-256 digest.",
                exception);
        }
    }

    public override string ToString()
    {
        Span<byte> value = stackalloc byte[SizeInBytes];
        BinaryPrimitives.WriteUInt64BigEndian(value, Part0);
        BinaryPrimitives.WriteUInt64BigEndian(value.Slice(8, 8), Part1);
        BinaryPrimitives.WriteUInt64BigEndian(value.Slice(16, 8), Part2);
        BinaryPrimitives.WriteUInt64BigEndian(value.Slice(24, 8), Part3);
        return Convert.ToHexString(value).ToLowerInvariant();
    }

    internal const int SizeInBytes = 32;
    private const int HexCharacterCount = SizeInBytes * 2;
}
