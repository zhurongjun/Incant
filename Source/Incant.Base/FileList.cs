namespace Incant.Base;

/// <summary>Provides a typed attachment whose contents are interpreted by the file list's consumer.</summary>
/// <remarks>Instances are retained by reference. Keep their contents stable after attaching them.</remarks>
public abstract class FileMetadata
{
}

/// <summary>A file path and its ordered metadata attachments in a resolved snapshot.</summary>
/// <remarks>The default value has an empty path and metadata list; resolved lists do not produce it.</remarks>
public readonly struct FileEntry
{
    private readonly string? _path;
    private readonly IReadOnlyList<FileMetadata>? _metadata;

    internal FileEntry(string path, IReadOnlyList<FileMetadata> metadata)
    {
        _path = path;
        _metadata = metadata;
    }

    /// <summary>Gets the lexically normalized absolute path, or an empty string for the default value.</summary>
    /// <remarks>Symbolic link entry points are retained.</remarks>
    public string Path => _path ?? string.Empty;

    /// <summary>Gets the read-only attachment list, including intentional duplicate objects and types.</summary>
    public IReadOnlyList<FileMetadata> Metadata => _metadata ?? Array.Empty<FileMetadata>();
}

/// <summary>Resolves an ordered sequence of file and metadata commands into a cached snapshot.</summary>
/// <remarks>
/// This type is not thread-safe. Commands do not perform filesystem I/O. Exact paths may identify
/// files that have not been generated yet. Globs use Microsoft.Extensions.FileSystemGlobbing syntax.
/// Path comparisons ignore case on Windows and are ordinal elsewhere. Filesystem changes require
/// a command change or an explicit <c>Resolve(force: true)</c> to become visible.
/// </remarks>
public sealed partial class FileList
{
    private static readonly StringComparer s_pathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly List<Command> _commands = [];
    private IReadOnlyList<FileEntry> _files = Array.Empty<FileEntry>();
    private bool _isResolved;

    /// <summary>Creates an empty list with an explicit, fixed path resolution base.</summary>
    /// <param name="rootDirectory">The base directory; a relative value is resolved at construction time.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rootDirectory"/> is null.</exception>
    /// <exception cref="ArgumentException">The directory is empty or is not a valid path.</exception>
    public FileList(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        try
        {
            RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The root directory is not a valid path.", nameof(rootDirectory), exception);
        }
    }

    /// <summary>Gets the fixed absolute base directory. It does not restrict files to this subtree.</summary>
    public string RootDirectory { get; }

    /// <summary>Gets the current read-only snapshot, resolving pending commands when needed.</summary>
    /// <remarks>Has the same filesystem side effects and failure behavior as <see cref="Resolve"/>.</remarks>
    public IReadOnlyList<FileEntry> Files => Resolve();

    /// <summary>Queues additions in argument order; each glob's matches are sorted before insertion.</summary>
    /// <remarks>Adding an existing path preserves its position and metadata. New entries have no metadata.</remarks>
    /// <param name="paths">Exact paths or glob patterns, relative to the root or absolute.</param>
    /// <returns>This list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> or an element is null.</exception>
    /// <exception cref="ArgumentException">An element is empty or is not a valid path or pattern.</exception>
    public FileList Add(params string[] paths) => EnqueuePaths(CommandKind.Add, paths);

    /// <summary>Queues removal of matching entries present at this point in the command sequence.</summary>
    /// <remarks>Matching uses the in-memory entries. Readding a removed file appends it with empty metadata.</remarks>
    /// <param name="paths">Exact paths or glob patterns, relative to the root or absolute.</param>
    /// <returns>This list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> or an element is null.</exception>
    /// <exception cref="ArgumentException">An element is empty or is not a valid path or pattern.</exception>
    public FileList Remove(params string[] paths) => EnqueuePaths(CommandKind.Remove, paths);

    /// <summary>Queues ordered metadata attachments for currently present files matching a selector.</summary>
    /// <param name="pattern">An exact path or glob, relative to the root or absolute.</param>
    /// <param name="metadata">Attachments to append, retaining their order and duplicates.</param>
    /// <returns>This list.</returns>
    /// <exception cref="ArgumentNullException">A required argument or metadata element is null.</exception>
    /// <exception cref="ArgumentException">The selector is empty or is not a valid path or pattern.</exception>
    public FileList SetMetadata(string pattern, params FileMetadata[] metadata)
    {
        Selector selector = CreateSelector(pattern, nameof(pattern));
        FileMetadata[] attachments = CopyMetadata(metadata);
        if (attachments.Length != 0)
        {
            _commands.Add(new Command(CommandKind.Metadata, selector, attachments));
            Invalidate();
        }
        return this;
    }

    /// <summary>Queues ordered metadata attachments for all files present at this point in the sequence.</summary>
    /// <param name="metadata">Attachments to append, retaining their order and duplicates.</param>
    /// <returns>This list.</returns>
    /// <exception cref="ArgumentNullException">The array or an element is null.</exception>
    public FileList SetMetadata(params FileMetadata[] metadata)
    {
        FileMetadata[] attachments = CopyMetadata(metadata);
        if (attachments.Length != 0)
        {
            _commands.Add(new Command(CommandKind.Metadata, null, attachments));
            Invalidate();
        }
        return this;
    }

    /// <summary>Discards every command and invalidates the snapshot. The next resolution is empty.</summary>
    /// <returns>This list.</returns>
    public FileList ClearCommands()
    {
        _commands.Clear();
        Invalidate();
        return this;
    }

    /// <summary>Replays commands when invalidated, then publishes a complete read-only snapshot.</summary>
    /// <param name="force">Whether to discard the cache and read the filesystem again.</param>
    /// <returns>The cached snapshot, or a new snapshot after successful resolution.</returns>
    /// <remarks>
    /// A failed replay remains unresolved and never publishes partial results. Previously returned
    /// snapshots remain unchanged. Recursive globs follow directory links but skip ancestor cycles.
    /// Exact paths are declarations and are not checked for existence. Unmatched globs are empty.
    /// </remarks>
    /// <exception cref="IOException">Filesystem enumeration or directory link resolution fails.</exception>
    /// <exception cref="UnauthorizedAccessException">A required directory cannot be accessed.</exception>
    public IReadOnlyList<FileEntry> Resolve(bool force = false)
    {
        if (_isResolved && !force)
        {
            return _files;
        }

        Invalidate();
        var state = new Resolution();
        var disk = new DiskTraversal();
        foreach (Command command in _commands)
        {
            if (command.Kind == CommandKind.Add)
            {
                Selector selector = command.Selector!;
                if (selector.Matcher is null)
                {
                    state.Add(selector.Path);
                }
                else
                {
                    foreach (string path in disk.Match(selector))
                    {
                        state.Add(path);
                    }
                }
                continue;
            }

            foreach (EntryState entry in state.Select(command.Selector))
            {
                if (command.Kind == CommandKind.Remove)
                {
                    state.Remove(entry);
                }
                else
                {
                    entry.Metadata ??= [];
                    entry.Metadata.AddRange(command.Metadata);
                }
            }
        }

        _files = state.Snapshot();
        _isResolved = true;
        return _files;
    }

    private FileList EnqueuePaths(CommandKind kind, string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var commands = new Command[paths.Length];
        for (int index = 0; index < paths.Length; index++)
        {
            commands[index] = new Command(kind, CreateSelector(paths[index], nameof(paths)), []);
        }
        if (commands.Length != 0)
        {
            _commands.AddRange(commands);
            Invalidate();
        }
        return this;
    }

    private static FileMetadata[] CopyMetadata(FileMetadata[] metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var copy = new FileMetadata[metadata.Length];
        for (int index = 0; index < metadata.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(metadata[index], nameof(metadata));
            copy[index] = metadata[index];
        }
        return copy;
    }

    private void Invalidate()
    {
        _isResolved = false;
        _files = Array.Empty<FileEntry>();
    }

    private enum CommandKind { Add, Remove, Metadata }

    private sealed record Command(CommandKind Kind, Selector? Selector, FileMetadata[] Metadata);

    private sealed class EntryState(string path)
    {
        internal string Path { get; } = path;

        internal List<FileMetadata>? Metadata { get; set; }

        internal bool Removed { get; set; }
    }

    private sealed class Resolution
    {
        private readonly Dictionary<string, EntryState> _entries = new(s_pathComparer);
        private readonly List<EntryState> _order = [];
        private MemoryIndex? _index;

        internal void Add(string path)
        {
            if (_entries.ContainsKey(path))
            {
                return;
            }
            var entry = new EntryState(path);
            _entries.Add(path, entry);
            _order.Add(entry);
            _index?.Add(path);
        }

        internal void Remove(EntryState entry)
        {
            _entries.Remove(entry.Path);
            entry.Removed = true;
            entry.Metadata = null;
            _index?.Remove(entry.Path);
        }

        internal IEnumerable<EntryState> Select(Selector? selector)
        {
            if (selector is null)
            {
                return _entries.Values;
            }
            if (selector.Matcher is null)
            {
                return _entries.TryGetValue(selector.Path, out EntryState? entry) ? [entry] : [];
            }

            if (_index is null)
            {
                _index = new MemoryIndex();
                foreach (string path in _entries.Keys)
                {
                    _index.Add(path);
                }
            }
            // Match returns a path snapshot, so removals cannot change the sequence being visited.
            return _index.Match(selector).Select(path => _entries[path]);
        }

        internal IReadOnlyList<FileEntry> Snapshot()
        {
            var files = new FileEntry[_entries.Count];
            int index = 0;
            foreach (EntryState entry in _order)
            {
                if (!entry.Removed)
                {
                    IReadOnlyList<FileMetadata> metadata = entry.Metadata is null
                        ? Array.Empty<FileMetadata>() : Array.AsReadOnly(entry.Metadata.ToArray());
                    files[index++] = new FileEntry(entry.Path, metadata);
                }
            }
            return Array.AsReadOnly(files);
        }
    }
}
