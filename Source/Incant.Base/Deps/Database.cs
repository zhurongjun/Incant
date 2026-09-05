namespace Incant.Base.Deps;

/// <summary>Tracks dependencies in a single append-only, checksum-verified record file.</summary>
/// <remarks>
/// Construction does not access files. An open writer owns the process lock until Close or scope disposal.
/// Read-only instances capture the complete records visible at each Open. Wait for all dependency operations
/// and callbacks before clearing or closing a database. A closed instance may be opened again.
/// Concurrent calls for the same key may both execute; the last successful append wins.
/// </remarks>
public sealed class Database
{
    /// <summary>Configures a closed single-file CSV dependency database without accessing files.</summary>
    /// <param name="path">The database file path.</param>
    /// <param name="cache">An optional caller-controlled file metadata cache.</param>
    /// <param name="defaultUseSHA">The default file comparison mode.</param>
    /// <param name="readOnly">Whether to open a fixed record snapshot without taking the writer lock.</param>
    /// <exception cref="ArgumentException">The path is invalid.</exception>
    public Database(
        string path,
        FileSnapshotCache? cache = null,
        bool defaultUseSHA = false,
        bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _cache = cache;
        _defaultUseSHA = defaultUseSHA;
        IsReadOnly = readOnly;
        _processLock = new FileLock(_path + ".lock");
    }

    /// <summary>Gets whether this instance is a fixed, read-only record snapshot.</summary>
    public bool IsReadOnly { get; }

    /// <summary>Gets whether this instance is open, including a faulted writer that still needs Close.</summary>
    public bool IsOpened
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    /// <summary>Loads a fresh snapshot and acquires the process lock when opening for writes.</summary>
    /// <remarks>
    /// A missing writable database, including its parent directory, is created with only a file header.
    /// Writers truncate invalid tail data and compact when damage precedes valid records.
    /// Read-only opens never modify files; a missing file opens as an empty snapshot.
    /// </remarks>
    /// <exception cref="InvalidOperationException">This instance is already open.</exception>
    /// <exception cref="IOException">Another writer owns the lock or a file operation failed.</exception>
    /// <exception cref="UnauthorizedAccessException">The database or its lock cannot be accessed.</exception>
    public void Open()
    {
        if (!TryOpen())
        {
            throw new IOException("Another writer owns the database lock.");
        }
    }

    /// <summary>Attempts to open without waiting for another writer's process lock.</summary>
    /// <returns>False only when another writer owns the lock; true when opening succeeds.</returns>
    /// <remarks>
    /// Creates a missing writable database just like Open. Other I/O failures propagate.
    /// Failed attempts leave this instance closed and retryable.
    /// </remarks>
    /// <exception cref="InvalidOperationException">This instance is already open.</exception>
    /// <exception cref="IOException">A file operation failed for a reason other than lock contention.</exception>
    /// <exception cref="UnauthorizedAccessException">The database or its lock cannot be accessed.</exception>
    public bool TryOpen()
    {
        lock (_gate)
        {
            return TryOpenCore();
        }
    }

    /// <summary>Opens a database and returns a scope that closes only this opening.</summary>
    /// <param name="database">The closed database to open.</param>
    /// <returns>A scope suitable for a using statement.</returns>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    /// <exception cref="InvalidOperationException">The database is already open.</exception>
    /// <remarks>Opening errors propagate exactly as they do from Open.</remarks>
    public static DatabaseScope OpenScoped(Database database)
    {
        ArgumentNullException.ThrowIfNull(database);
        lock (database._gate)
        {
            database.Open();
            return new DatabaseScope(database, database._session!);
        }
    }

    /// <summary>Durably flushes a healthy writer, releases files and locks, and clears the loaded snapshot.</summary>
    /// <remarks>
    /// Closing is idempotent and allows another Open. Wait for all dependency operations and callbacks first.
    /// Resources are released even if flushing fails. The caller-controlled metadata cache is not cleared.
    /// </remarks>
    public void Close()
    {
        lock (_gate)
        {
            CloseCore();
        }
    }

    /// <summary>Gets the number of keys with a complete, valid record.</summary>
    /// <exception cref="InvalidOperationException">The database is closed or faulted.</exception>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                EnsureUsable();
                return _records.Count;
            }
        }
    }

    /// <summary>Gets the number of valid physical records, including superseded versions.</summary>
    /// <remarks>Corrupt records are excluded. Compaction resets this number to Count.</remarks>
    /// <exception cref="InvalidOperationException">The database is closed or faulted.</exception>
    public long TotalRecordCount
    {
        get
        {
            lock (_gate)
            {
                EnsureUsable();
                return _totalRecordCount;
            }
        }
    }

    /// <summary>Checks whether a record is missing, forced, or has changed dependencies.</summary>
    /// <param name="key">The dependency key, normalized to lowercase.</param>
    /// <param name="files">The unordered input file set.</param>
    /// <param name="args">The ordered arguments.</param>
    /// <param name="options">Optional settings that replace the defaults.</param>
    /// <returns>True when an action would need to run.</returns>
    /// <exception cref="InvalidOperationException">The database is closed or faulted.</exception>
    public bool IsOutdated(
        string key, IEnumerable<string>? files, IEnumerable<string>? args, CheckOptions? options = null)
    {
        Record input = RecordUtils.CreateRecord(key, files, args, options?.UseSHA ?? _defaultUseSHA);
        Record? saved = FindRecord(input.Key);
        return options?.Force == true || RecordUtils.IsOutdated(input, saved, _cache);
    }

    /// <summary>Executes an outdated dependency action and appends its completed record.</summary>
    /// <param name="key">The dependency key.</param>
    /// <param name="action">The callback, which may add external files.</param>
    /// <param name="files">The unordered input file set.</param>
    /// <param name="args">The ordered arguments.</param>
    /// <param name="options">Optional settings that replace the defaults.</param>
    /// <returns>True when the callback ran; false when the record is current.</returns>
    /// <exception cref="InvalidOperationException">The database is closed, read-only, or faulted.</exception>
    /// <remarks>Callback exceptions propagate without saving. Automatic compaction errors can follow a committed append.</remarks>
    public bool RunIfOutdated(
        string key, Action<Record> action, IEnumerable<string>? files,
        IEnumerable<string>? args, CheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (PrepareOutdatedRecord(key, files, args, options) is not Record record)
        {
            return false;
        }

        action(record);
        AppendRecord(record);
        return true;
    }

    /// <summary>Awaits an outdated dependency action before appending its completed record.</summary>
    /// <param name="key">The dependency key.</param>
    /// <param name="action">The asynchronous callback, which may add external files.</param>
    /// <param name="files">The unordered input file set.</param>
    /// <param name="args">The ordered arguments.</param>
    /// <param name="options">Optional settings that replace the defaults.</param>
    /// <returns>True when the callback ran; false when the record is current.</returns>
    /// <exception cref="InvalidOperationException">The database is closed, read-only, or faulted.</exception>
    /// <remarks>The overload retains its original name for API compatibility.</remarks>
    public async Task<bool> RunIfOutdated(
        string key, Func<Record, Task> action, IEnumerable<string>? files,
        IEnumerable<string>? args, CheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (PrepareOutdatedRecord(key, files, args, options) is not Record record)
        {
            return false;
        }

        await action(record).ConfigureAwait(false);
        AppendRecord(record);
        return true;
    }

    /// <summary>Flushes the writer, optionally requesting durable storage from the OS.</summary>
    /// <param name="flushToDisk">Whether to request a durable flush; defaults to true.</param>
    /// <exception cref="InvalidOperationException">The database is closed, read-only, or faulted.</exception>
    public void Flush(bool flushToDisk = true)
    {
        lock (_gate)
        {
            EnsureWritable();
            try
            {
                _stream!.Flush(flushToDisk);
            }
            catch (IOException)
            {
                _faulted = true;
                throw;
            }
        }
    }

    /// <summary>Atomically replaces the file with the latest complete record for each key.</summary>
    /// <exception cref="InvalidOperationException">The database is closed, read-only, or faulted.</exception>
    public void Compact()
    {
        lock (_gate)
        {
            EnsureWritable();
            ReplaceContents(clear: false);
        }
    }

    /// <summary>Atomically replaces all records with an empty file and clears the metadata cache.</summary>
    /// <remarks>The caller must wait for all dependency callbacks before clearing.</remarks>
    /// <exception cref="InvalidOperationException">The database is closed, read-only, or faulted.</exception>
    public void ClearDatabase()
    {
        lock (_gate)
        {
            EnsureWritable();
            ReplaceContents(clear: true);
            _cache?.Clear();
        }
    }

    internal void Close(object session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
            {
                CloseCore();
            }
        }
    }

    private bool TryOpenCore()
    {
        if (_session is not null)
        {
            throw new InvalidOperationException("The database is already open.");
        }

        if (Directory.Exists(_path))
        {
            throw new IOException("The database path is a directory rather than a file.");
        }

        if (!IsReadOnly && !_processLock.TryLock())
        {
            return false;
        }

        try
        {
            if (IsReadOnly)
            {
                LoadReadOnly();
            }
            else
            {
                _stream = DatabaseFileUtils.OpenWriter(_path, FileMode.OpenOrCreate);
                if (!DatabaseFileUtils.ReadRecords(_stream, _records, out _totalRecordCount, out long validLength))
                {
                    ReplaceContents(clear: false);
                }
                else
                {
                    // Damage confined to the tail can be discarded without rewriting earlier records.
                    if (_stream.Length != validLength)
                    {
                        _stream.SetLength(validLength);
                    }

                    _stream.Seek(0, SeekOrigin.End);
                    CompactIfNeeded();
                }
            }

            // A session identity survives stream replacement during compaction, but never a reopen.
            _session = new object();
            return true;
        }
        catch
        {
            ReleaseResources();
            throw;
        }
    }

    private void CloseCore()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            if (!_faulted)
            {
                _stream?.Flush(flushToDisk: true);
            }
        }
        finally
        {
            ReleaseResources();
        }
    }

    private void ReleaseResources()
    {
        FileStream? stream = _stream;
        _stream = null;
        _session = null;
        _records.Clear();
        _totalRecordCount = 0;
        _faulted = false;

        try
        {
            stream?.Dispose();
        }
        finally
        {
            _processLock.Unlock();
        }
    }

    private Record? PrepareOutdatedRecord(
        string key, IEnumerable<string>? files, IEnumerable<string>? args, CheckOptions? options)
    {
        Record? saved;
        Record record = RecordUtils.CreateRecord(key, files, args, options?.UseSHA ?? _defaultUseSHA);
        lock (_gate)
        {
            EnsureWritable();
            saved = _records.TryGetValue(record.Key, out Record value) ? value : null;
        }

        if (options?.Force != true && !RecordUtils.IsOutdated(record, saved, _cache))
        {
            return null;
        }

        // Inputs describe the state before the action; external files are captured after it completes.
        RecordUtils.CaptureFiles(record.Files, record.UseSHA, _cache);
        return record;
    }

    private Record? FindRecord(string key)
    {
        lock (_gate)
        {
            EnsureUsable();
            return _records.TryGetValue(key, out Record record) ? record : null;
        }
    }

    private void AppendRecord(Record record)
    {
        RecordUtils.CaptureFiles(record.ExternalFiles, record.UseSHA, _cache);

        // Only this collection is mutable through a retained callback record.
        Record snapshot = record with { ExternalFiles = [.. record.ExternalFiles] };
        lock (_gate)
        {
            EnsureWritable();
            try
            {
                DatabaseFileUtils.AppendRecord(_stream!, snapshot);
            }
            catch (IOException)
            {
                _faulted = true;
                throw;
            }

            _records[snapshot.Key] = snapshot;
            _totalRecordCount++;
            CompactIfNeeded();
        }
    }

    private void LoadReadOnly()
    {
        try
        {
            using FileStream stream = DatabaseFileUtils.OpenReadOnly(_path);
            DatabaseFileUtils.ReadRecords(stream, _records, out _totalRecordCount, out _);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // A missing read-only database is an empty snapshot.
        }
    }

    private void CompactIfNeeded()
    {
        if (_totalRecordCount > 1000 && _totalRecordCount > _records.Count * 3L)
        {
            ReplaceContents(clear: false);
        }
    }

    private void ReplaceContents(bool clear)
    {
        IEnumerable<Record> records = clear
            ? []
            : _records.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => pair.Value);
        DatabaseFileUtils.WriteCompactedFile(_path, records);

        _faulted = true;
        _stream!.Dispose();
        _stream = null;
        try
        {
            DatabaseFileUtils.ReplaceWithCompactedFile(_path);
            if (clear)
            {
                _records.Clear();
            }

            _totalRecordCount = _records.Count;
        }
        finally
        {
            // Whether replacement succeeded or failed, reopen the file that now owns the database path.
            // A failed reopen leaves the writer faulted until the caller closes it.
            _stream = DatabaseFileUtils.OpenWriter(_path, FileMode.Open);
            _stream.Seek(0, SeekOrigin.End);
            _faulted = false;
        }
    }

    private void EnsureUsable()
    {
        if (_session is null)
        {
            throw new InvalidOperationException("The database is closed.");
        }

        if (_faulted)
        {
            throw new InvalidOperationException("A database I/O operation failed; close and reopen the database.");
        }
    }

    private void EnsureWritable()
    {
        EnsureUsable();
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The database is read-only.");
        }
    }

    private readonly object _gate = new();

    private readonly string _path;

    private readonly FileSnapshotCache? _cache;

    private readonly bool _defaultUseSHA;

    private readonly FileLock _processLock;

    private readonly Dictionary<string, Record> _records = new(StringComparer.Ordinal);

    private FileStream? _stream;

    private long _totalRecordCount;

    private bool _faulted;

    private object? _session;
}

/// <summary>Closes the specific database opening owned by this scope.</summary>
/// <remarks>
/// Obtain a scope through Database.OpenScoped. Default and copied scopes are safe to dispose repeatedly.
/// Closing and reopening the database makes an earlier scope inert. Await dependency operations before disposal.
/// </remarks>
public readonly struct DatabaseScope : IDisposable
{
    internal DatabaseScope(Database owner, object session)
    {
        _owner = owner;
        _session = session;
    }

    /// <summary>Closes the owned opening, without affecting a later opening of the same database.</summary>
    public void Dispose()
    {
        if (_session is not null)
        {
            _owner!.Close(_session);
        }
    }

    private readonly Database? _owner;

    private readonly object? _session;
}
