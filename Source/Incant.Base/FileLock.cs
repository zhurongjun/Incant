using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Incant.Base;

/// <summary>Describes a reusable, non-reentrant exclusive file lock.</summary>
/// <remarks>
/// Construction performs no file I/O. Lock and TryLock attempt immediately; neither waits for another owner.
/// Instance operations are thread-safe and have no thread affinity. Call Unlock, or use LockScoped, to release ownership.
/// Cooperating callers must not delete or replace the lock file. Unix locks are advisory, and remote filesystem
/// guarantees depend on the server and mount configuration; this is not a distributed lease or fencing mechanism.
/// </remarks>
public sealed class FileLock
{
    /// <summary>Creates an unlocked descriptor and resolves its path without accessing the file.</summary>
    /// <param name="path">The lock file path itself; no suffix is appended. Relative paths use the current directory.</param>
    /// <exception cref="ArgumentException">The path is null, empty, whitespace, or invalid.</exception>
    public FileLock(string path)
    {
        Path = GetFullPath(path);
        _nativePath = GetNativePath(Path);
    }

    /// <summary>Gets the absolute lock file path.</summary>
    public string Path { get; }

    /// <summary>Gets whether this object currently owns the lock, without querying the filesystem.</summary>
    public bool OwnsLock
    {
        get
        {
            lock (_gate)
            {
                return _stream is not null;
            }
        }
    }

    /// <summary>Immediately acquires ownership, creating the file and missing parent directories when needed.</summary>
    /// <exception cref="InvalidOperationException">This object already owns the lock.</exception>
    /// <exception cref="IOException">The lock is busy or a filesystem operation fails.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="PlatformNotSupportedException">The operating system is unsupported.</exception>
    /// <remarks>Does not wait, retry contention, truncate the file, or change existing file permissions.</remarks>
    public void Lock()
    {
        lock (_gate)
        {
            LockCore();
        }
    }

    /// <summary>Immediately attempts to acquire ownership without throwing for contention.</summary>
    /// <returns>True after acquiring ownership; false if another handle owns an incompatible lock.</returns>
    /// <exception cref="InvalidOperationException">This object already owns the lock.</exception>
    /// <exception cref="IOException">A filesystem operation fails for a reason other than contention.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="PlatformNotSupportedException">The operating system is unsupported.</exception>
    /// <remarks>Creates missing parent directories and the lock file. Failure leaves this object unlocked.</remarks>
    public bool TryLock()
    {
        lock (_gate)
        {
            return TryLockCore();
        }
    }

    /// <summary>Releases this object's current lock without deleting the file. Does nothing when already unlocked.</summary>
    /// <exception cref="IOException">Releasing the operating system lock fails; the handle is still closed.</exception>
    public void Unlock()
    {
        lock (_gate)
        {
            UnlockCore();
        }
    }

    /// <summary>Acquires a new lock descriptor and returns a scope that releases that acquisition.</summary>
    /// <param name="path">The lock file path itself; no suffix is appended.</param>
    /// <returns>The acquired scope, which the caller must dispose.</returns>
    /// <remarks>Uses the same immediate acquisition and error behavior as Lock.</remarks>
    public static FileLockScope LockScoped(string path) => LockScoped(new FileLock(path));

    /// <summary>Acquires an existing descriptor and returns a scope bound to that particular acquisition.</summary>
    /// <param name="fileLock">An unlocked descriptor to acquire.</param>
    /// <returns>The acquired scope, which the caller must dispose.</returns>
    /// <exception cref="ArgumentNullException">The descriptor is null.</exception>
    /// <remarks>Uses the same immediate acquisition and error behavior as Lock; does not adopt an existing lock.</remarks>
    public static FileLockScope LockScoped(FileLock fileLock)
    {
        ArgumentNullException.ThrowIfNull(fileLock);
        lock (fileLock._gate)
        {
            return new FileLockScope(fileLock, fileLock.LockCore());
        }
    }

    /// <summary>Checks whether an existing lock file currently rejects an exclusive lock attempt.</summary>
    /// <param name="path">The lock file path itself; no suffix is appended.</param>
    /// <returns>True on contention; false if the file is available or its file or parent directory is missing.</returns>
    /// <exception cref="ArgumentException">The path is null, empty, whitespace, or invalid.</exception>
    /// <exception cref="IOException">A filesystem or locking error other than contention occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="PlatformNotSupportedException">The operating system is unsupported.</exception>
    /// <remarks>
    /// Does not create files or directories, or change file contents. A successful probe briefly holds and then releases
    /// the lock. The answer can immediately become stale; use TryLock to acquire ownership instead of checking first.
    /// A retained lock file alone does not imply ownership.
    /// </remarks>
    public static bool IsLocked(string path)
    {
        string fullPath = GetFullPath(path);
        SafeFileHandle? handle = TryOpenLockedHandle(fullPath, GetNativePath(fullPath), create: false, out bool missing);
        if (handle is null)
        {
            return !missing;
        }

        try
        {
            UnlockHandle(handle, fullPath);
        }
        finally
        {
            handle.Dispose();
        }

        return false;
    }

    internal void Unlock(FileStream acquisition)
    {
        lock (_gate)
        {
            // A copied or delayed scope must never release a subsequent acquisition.
            if (ReferenceEquals(_stream, acquisition))
            {
                UnlockCore();
            }
        }
    }

    private FileStream LockCore()
    {
        if (!TryLockCore())
        {
            throw new IOException($"The lock file '{Path}' is already in use.");
        }

        return _stream!;
    }

    private bool TryLockCore()
    {
        if (_stream is not null)
        {
            throw new InvalidOperationException("This object already owns the file lock.");
        }

        SafeFileHandle? handle = TryOpenLockedHandle(Path, _nativePath, create: true, out _);
        if (handle is null)
        {
            return false;
        }

        try
        {
            // No file contents are read or written; disable FileStream's data buffer.
            _stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
            return true;
        }
        catch
        {
            try
            {
                UnlockHandle(handle, Path);
            }
            finally
            {
                handle.Dispose();
            }

            throw;
        }
    }

    private void UnlockCore()
    {
        if (_stream is null)
        {
            return;
        }

        try
        {
            UnlockHandle(_stream.SafeFileHandle, Path);
        }
        finally
        {
            try
            {
                _stream.Dispose();
            }
            finally
            {
                _stream = null;
            }
        }
    }

    private static SafeFileHandle? TryOpenLockedHandle(string path, string nativePath, bool create, out bool missing)
    {
        if (OperatingSystem.IsWindows())
        {
            return TryOpenWindows(path, nativePath, create, out missing);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return TryOpenUnix(path, create, out missing);
        }

        throw new PlatformNotSupportedException("File locks are supported on Windows, Linux, and macOS.");
    }

    private static SafeFileHandle? TryOpenWindows(string path, string nativePath, bool create, out bool missing)
    {
        missing = false;
        bool createdParents = false;
        while (true)
        {
            SafeFileHandle handle = Native.CreateFile(
                nativePath, Native.GenericReadWrite, 0, IntPtr.Zero,
                create ? Native.OpenAlways : Native.OpenExisting, Native.NormalAttributes, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                return handle;
            }

            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is Native.WindowsSharingViolation or Native.WindowsLockViolation)
            {
                return null;
            }

            if (create && !createdParents && error == Native.WindowsPathNotFound)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                createdParents = true;
                continue;
            }

            if (!create && error is Native.WindowsFileNotFound or Native.WindowsPathNotFound)
            {
                missing = true;
                return null;
            }

            throw CreateIoError(path, error);
        }
    }

    private static SafeFileHandle? TryOpenUnix(string path, bool create, out bool missing)
    {
        missing = false;
        int flags = Native.ReadWrite | (OperatingSystem.IsLinux() ? Native.LinuxCloseOnExec : Native.MacOsCloseOnExec);
        int descriptor;
        int error;
        do
        {
            descriptor = Native.OpenExistingFile(path, flags);
            error = descriptor < 0 ? Marshal.GetLastPInvokeError() : 0;
        } while (descriptor < 0 && error == Native.Interrupted);

        SafeFileHandle handle;
        if (descriptor >= 0)
        {
            handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }
        else if (create && error == Native.NoEntry)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            try
            {
                // Creation is cold. Let the BCL handle modes, umask, and macOS ARM64's variadic open ABI.
                // The existing-file path above needs neither O_CREAT nor its variadic mode argument.
                handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch (IOException exception) when (IsUnixContention(exception.HResult))
            {
                return null;
            }
        }
        else if (!create && error is Native.NoEntry or Native.NotDirectory)
        {
            missing = true;
            return null;
        }
        else
        {
            throw CreateIoError(path, error);
        }

        try
        {
            error = Flock(handle, Native.Exclusive | Native.NonBlocking);
            if (error == 0)
            {
                return handle;
            }

            if (IsUnixContention(error))
            {
                handle.Dispose();
                return null;
            }

            // Unlike FileShare's best-effort Unix implementation, unsupported locks must fail closed.
            throw CreateIoError(path, error);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void UnlockHandle(SafeFileHandle handle, string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            int error = Flock(handle, Native.Unlock);
            if (error != 0)
            {
                throw CreateIoError(path, error);
            }
        }
    }

    private static int Flock(SafeFileHandle handle, int operation)
    {
        int error;
        do
        {
            if (Native.Flock(handle, operation) == 0)
            {
                return 0;
            }

            error = Marshal.GetLastPInvokeError();
        } while (error == Native.Interrupted);

        return error;
    }

    private static bool IsUnixContention(int error) =>
        error == (OperatingSystem.IsMacOS() ? Native.MacOsWouldBlock : Native.LinuxWouldBlock);

    private static Exception CreateIoError(string path, int error)
    {
        bool windows = OperatingSystem.IsWindows();
        string message = $"Cannot access lock file '{path}': {new Win32Exception(error).Message}";
        if (windows ? error == Native.WindowsAccessDenied : error is Native.PermissionDenied or Native.OperationNotPermitted)
        {
            return Directory.Exists(path)
                ? new IOException("The lock path is a directory rather than a file.")
                : new UnauthorizedAccessException(message);
        }

        if (windows ? error == Native.WindowsFileNotFound : error == Native.NoEntry)
        {
            return new FileNotFoundException(message, path);
        }

        if (windows ? error == Native.WindowsPathNotFound : error == Native.NotDirectory)
        {
            return new DirectoryNotFoundException(message);
        }

        int code = windows ? unchecked((int)(0x80070000u | (uint)error)) : error;
        return new IOException(message, code);
    }

    private static string GetFullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return System.IO.Path.GetFullPath(path);
    }

    private static string GetNativePath(string path)
    {
        if (!OperatingSystem.IsWindows() || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        // Preserve .NET's long-path support when calling CreateFileW directly.
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string path, uint access, uint share, IntPtr security, uint disposition, uint attributes, IntPtr template);

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        internal static extern int OpenExistingFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
        internal static extern int Flock(SafeFileHandle handle, int operation);

        internal const uint GenericReadWrite = 0xC0000000;

        internal const uint OpenExisting = 3;

        internal const uint OpenAlways = 4;

        internal const uint NormalAttributes = 0x80;

        internal const int WindowsFileNotFound = 2;

        internal const int WindowsPathNotFound = 3;

        internal const int WindowsAccessDenied = 5;

        internal const int WindowsSharingViolation = 32;

        internal const int WindowsLockViolation = 33;

        internal const int ReadWrite = 2;

        internal const int LinuxCloseOnExec = 0x80000;

        internal const int MacOsCloseOnExec = 0x1000000;

        internal const int OperationNotPermitted = 1;

        internal const int NoEntry = 2;

        internal const int Interrupted = 4;

        internal const int PermissionDenied = 13;

        internal const int NotDirectory = 20;

        internal const int LinuxWouldBlock = 11;

        internal const int MacOsWouldBlock = 35;

        internal const int Exclusive = 2;

        internal const int NonBlocking = 4;

        internal const int Unlock = 8;
    }

    private readonly object _gate = new();

    private readonly string _nativePath;

    private FileStream? _stream;
}

/// <summary>Releases one specific FileLock acquisition when disposed.</summary>
/// <remarks>
/// Copies share the same acquisition: disposing any copy releases it, and later copies do nothing.
/// A default scope does nothing. A stale scope cannot release a lock acquired after an explicit Unlock.
/// </remarks>
public readonly struct FileLockScope : IDisposable
{
    internal FileLockScope(FileLock owner, FileStream acquisition)
    {
        _owner = owner;
        _acquisition = acquisition;
    }

    /// <summary>Releases this scope's acquisition if it is still owned.</summary>
    public void Dispose()
    {
        if (_owner is not null && _acquisition is not null)
        {
            _owner.Unlock(_acquisition);
        }
    }

    private readonly FileLock? _owner;

    private readonly FileStream? _acquisition;
}
