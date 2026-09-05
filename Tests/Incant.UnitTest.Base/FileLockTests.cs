using System.Diagnostics;
using Incant.Base;

namespace Incant.UnitTest.Base;

public sealed class FileLockTests : IDisposable
{
    public FileLockTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Incant.UnitTest.Base", "FileLock", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void ConstructionOnlyCreatesAnUnlockedDescriptor()
    {
        string path = Path.Combine(_directory, "missing", "resource.lock");
        var fileLock = new FileLock(path);
        Assert.Equal(Path.GetFullPath(path), fileLock.Path);
        Assert.False(fileLock.OwnsLock);
        fileLock.Unlock();
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public void LockAndUnlockReuseTheDescriptorAndKeepTheExactFileName()
    {
        string path = Path.Combine(_directory, "nested", "deeper", "锁文件");
        var fileLock = new FileLock(path);
        try
        {
            for (int iteration = 0; iteration < 3; iteration++)
            {
                fileLock.Lock();
                Assert.True(fileLock.OwnsLock);
                Assert.True(File.Exists(path));
                Assert.False(File.Exists(path + ".lock"));
                Assert.Throws<InvalidOperationException>(fileLock.Lock);
                Assert.Throws<InvalidOperationException>(() => fileLock.TryLock());
                fileLock.Unlock();
                fileLock.Unlock();
                Assert.False(fileLock.OwnsLock);
            }
        }
        finally
        {
            fileLock.Unlock();
        }

        Assert.True(File.Exists(path));
        Assert.Empty(File.ReadAllBytes(path));
    }

    [Fact]
    public void ExistingLockFileContentsArePreserved()
    {
        byte[] contents = [0, 1, 2, 127, 255];
        File.WriteAllBytes(LockPath, contents);
        AcquireAndRelease(LockPath);
        Assert.True(TryLockAndUnlock(LockPath));
        Assert.Equal(contents, File.ReadAllBytes(LockPath));
    }

    [Fact]
    public void QueryingMissingLocksDoesNotCreateFilesOrDirectories()
    {
        Assert.False(FileLock.IsLocked(LockPath));
        Assert.False(FileLock.IsLocked(Path.Combine(_directory, "missing", "resource.lock")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public void StatusQueriesTrackOwnershipWithoutChangingTheFileOrKeepingTheLock()
    {
        byte[] contents = [0, 1, 2, 127, 255];
        File.WriteAllBytes(LockPath, contents);
        Assert.False(FileLock.IsLocked(LockPath));
        Assert.False(FileLock.IsLocked(LockPath));
        var owner = new FileLock(LockPath);
        using (FileLock.LockScoped(owner))
        {
            Assert.True(owner.OwnsLock);
            Assert.True(FileLock.IsLocked(LockPath));
            Assert.True(FileLock.IsLocked(Path.Combine(_directory, ".", "resource.lock")));
            var observer = new FileLock(LockPath);
            Assert.False(observer.OwnsLock);
            observer.Unlock();
            Assert.True(owner.OwnsLock);
        }

        Assert.False(owner.OwnsLock);
        Assert.False(FileLock.IsLocked(LockPath));
        Assert.Equal(contents, File.ReadAllBytes(LockPath));
        AcquireAndRelease(LockPath);
    }

    [Fact]
    public void TryLockCreatesAndRetainsOwnershipAndCanRetryContention()
    {
        string path = Path.Combine(_directory, "missing", "resource.lock");
        var owner = new FileLock(path);
        var contender = new FileLock(path);
        try
        {
            Assert.True(owner.TryLock());
            Assert.True(owner.OwnsLock);
            Assert.True(FileLock.IsLocked(path));
            Assert.False(contender.TryLock());
            Assert.False(contender.OwnsLock);
            Assert.ThrowsAny<IOException>(contender.Lock);
            Assert.False(contender.OwnsLock);
            owner.Unlock();
            Assert.True(contender.TryLock());
            Assert.True(contender.OwnsLock);
            Assert.False(owner.OwnsLock);
        }
        finally
        {
            contender.Unlock();
            owner.Unlock();
        }

        Assert.False(FileLock.IsLocked(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void DifferentLockFilesHaveIndependentOwners()
    {
        var first = new FileLock(LockPath);
        string otherPath = Path.Combine(_directory, "other.lock");
        var second = new FileLock(otherPath);
        using FileLockScope firstScope = FileLock.LockScoped(first);
        using FileLockScope secondScope = FileLock.LockScoped(second);
        Assert.False(TryLockAndUnlock(LockPath));
        Assert.False(TryLockAndUnlock(otherPath));
        first.Unlock();
        AcquireAndRelease(LockPath);
        Assert.False(TryLockAndUnlock(otherPath));
        Assert.True(second.OwnsLock);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("\0")]
    public void InvalidPathsAreRejectedWithoutCreatingFiles(string? path)
    {
        ArgumentException exception = Assert.ThrowsAny<ArgumentException>(() => new FileLock(path!));
        Assert.Equal("path", exception.ParamName);
        Assert.ThrowsAny<ArgumentException>(() => FileLock.IsLocked(path!));
        Assert.ThrowsAny<ArgumentException>(() => AcquireAndRelease(path!));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public void DirectoryIsRejectedWhenLockingRatherThanConstructing()
    {
        var descriptor = new FileLock(_directory);
        Assert.False(descriptor.OwnsLock);
        Assert.ThrowsAny<IOException>(descriptor.Lock);
        Assert.ThrowsAny<IOException>(() => descriptor.TryLock());
        Assert.ThrowsAny<IOException>(() => FileLock.IsLocked(_directory));
        Assert.False(descriptor.OwnsLock);
        descriptor.Unlock();
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public void AFileBlockingTheParentDirectoryIsNotOverwritten()
    {
        string parent = Path.Combine(_directory, "parent");
        File.WriteAllText(parent, "keep");
        var descriptor = new FileLock(Path.Combine(parent, "resource.lock"));
        Assert.ThrowsAny<IOException>(descriptor.Lock);
        Assert.ThrowsAny<IOException>(() => descriptor.TryLock());
        Assert.False(descriptor.OwnsLock);
        Assert.Equal("keep", File.ReadAllText(parent));
    }

    [Fact]
    public void ReadOnlyAccessFailureIsNotReportedAsContentionOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows read-only attributes restrict opening a file for writing.");
        File.WriteAllText(LockPath, "keep");
        FileAttributes attributes = File.GetAttributes(LockPath);
        var descriptor = new FileLock(LockPath);
        try
        {
            File.SetAttributes(LockPath, attributes | FileAttributes.ReadOnly);
            Assert.Throws<UnauthorizedAccessException>(descriptor.Lock);
            Assert.Throws<UnauthorizedAccessException>(() => descriptor.TryLock());
            Assert.Throws<UnauthorizedAccessException>(() => FileLock.IsLocked(LockPath));
            Assert.False(descriptor.OwnsLock);
        }
        finally
        {
            File.SetAttributes(LockPath, attributes);
            descriptor.Unlock();
        }

        AcquireAndRelease(LockPath);
        Assert.False(FileLock.IsLocked(LockPath));
        Assert.Equal("keep", File.ReadAllText(LockPath));
    }

    [Fact]
    public void ScopeReleasesOwnershipWhenTheProtectedOperationThrows()
    {
        var owner = new FileLock(LockPath);
        Action operation = () =>
        {
            using FileLockScope scope = FileLock.LockScoped(owner);
            throw new InvalidOperationException("The protected operation failed.");
        };
        Assert.Throws<InvalidOperationException>(operation);
        Assert.False(owner.OwnsLock);
        AcquireAndRelease(LockPath);
    }

    [Fact]
    public void DefaultAndCopiedScopesAreSafeAcrossLaterAcquisitions()
    {
        default(FileLockScope).Dispose();
        var owner = new FileLock(LockPath);
        using FileLockScope first = FileLock.LockScoped(owner);
        FileLockScope copy = first;
        copy.Dispose();
        Assert.False(owner.OwnsLock);
        using FileLockScope next = FileLock.LockScoped(owner);
        first.Dispose();
        copy.Dispose();
        Assert.True(owner.OwnsLock);
        Assert.False(TryLockAndUnlock(LockPath));
    }

    [Fact]
    public void OldScopeCannotReleaseALockAcquiredAfterManualUnlock()
    {
        var owner = new FileLock(LockPath);
        using FileLockScope old = FileLock.LockScoped(owner);
        owner.Unlock();
        owner.Lock();
        try
        {
            old.Dispose();
            Assert.True(owner.OwnsLock);
            Assert.True(FileLock.IsLocked(LockPath));
        }
        finally
        {
            owner.Unlock();
        }
    }

    [Fact]
    public void ScopeRejectsNullAndAlreadyOwnedDescriptors()
    {
        Assert.Throws<ArgumentNullException>(() => FileLock.LockScoped((FileLock)null!));
        var owner = new FileLock(LockPath);
        using FileLockScope scope = FileLock.LockScoped(owner);
        Assert.Throws<InvalidOperationException>(() => FileLock.LockScoped(owner));
        Assert.True(owner.OwnsLock);
    }

    [Fact]
    public async Task OtherThreadsCannotAcquireAnOwnedLockAndCanReleaseItsScope()
    {
        var owner = new FileLock(LockPath);
        using FileLockScope scope = FileLock.LockScoped(owner);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            Assert.True(owner.OwnsLock);
            Assert.True(FileLock.IsLocked(LockPath));
            Assert.False(TryLockAndUnlock(LockPath));
        }, TestContext.Current.CancellationToken)));

        await Task.Run(scope.Dispose, TestContext.Current.CancellationToken);
        Assert.False(owner.OwnsLock);
        Assert.False(FileLock.IsLocked(LockPath));
        AcquireAndRelease(LockPath);
    }

    [Fact]
    public async Task ConcurrentAcquisitionsOnOneDescriptorHaveExactlyOneOwner()
    {
        var owner = new FileLock(LockPath);
        try
        {
            Exception?[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                Xunit.Record.Exception(() => Assert.True(owner.TryLock())), TestContext.Current.CancellationToken)));
            Assert.Single(results, static result => result is null);
            Assert.All(results.Where(static result => result is not null),
                static result => Assert.IsType<InvalidOperationException>(result));
            Assert.True(owner.OwnsLock);
        }
        finally
        {
            owner.Unlock();
        }

        Assert.False(FileLock.IsLocked(LockPath));
    }

    [Fact]
    public void NativeLocksInteroperateWithExistingFileShareLocks()
    {
        var owner = new FileLock(LockPath);
        using (var stream = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(FileLock.IsLocked(LockPath));
            Assert.False(owner.TryLock());
            Assert.False(owner.OwnsLock);
        }

        using FileLockScope scope = FileLock.LockScoped(owner);
        Assert.ThrowsAny<IOException>(() =>
        {
            using var stream = new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
        Assert.True(owner.OwnsLock);
    }

    [Fact]
    public async Task IndependentThreadDescriptorsHaveExactlyOneWinner()
    {
        FileLock[] contenders = Enumerable.Range(0, 8).Select(_ => new FileLock(LockPath)).ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>[] attempts = contenders.Select(candidate => Task.Run(async () =>
        {
            await start.Task;
            return candidate.TryLock();
        }, TestContext.Current.CancellationToken)).ToArray();
        try
        {
            start.SetResult();
            bool[] results = await Task.WhenAll(attempts);
            Assert.Single(results, static acquired => acquired);
            Assert.Single(contenders, static candidate => candidate.OwnsLock);
            for (int index = 0; index < contenders.Length; index++)
            {
                Assert.Equal(results[index], contenders[index].OwnsLock);
            }
        }
        finally
        {
            start.TrySetResult();
            // Join all attempts before releasing descriptors, including during test cancellation.
            try
            {
                await Task.WhenAll(attempts);
            }
            finally
            {
                foreach (FileLock candidate in contenders)
                {
                    candidate.Unlock();
                }
            }
        }

        Assert.False(FileLock.IsLocked(LockPath));
    }

    [Fact]
    public async Task ConcurrentOldScopeCopiesCannotReleaseANewAcquisition()
    {
        var owner = new FileLock(LockPath);
        using FileLockScope old = FileLock.LockScoped(owner);
        old.Dispose();
        using FileLockScope current = FileLock.LockScoped(owner);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(old.Dispose, TestContext.Current.CancellationToken)));
        Assert.True(owner.OwnsLock);
        Assert.False(TryLockAndUnlock(LockPath));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProcessesCompeteAndHandOffOwnershipAfterReleaseOrTermination(
        bool terminateFirstWinner, bool disableRuntimeLocking)
    {
        var children = new List<Process>();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            for (int index = 0; index < 4; index++)
            {
                children.Add(StartLockOwner(LockPath, disableRuntimeLocking, command: "file-lock-contend"));
            }

            string?[] ready = await Task.WhenAll(children.Select(child => child.StandardOutput.ReadLineAsync(timeout.Token).AsTask()));
            Assert.All(ready, static message => Assert.Equal("ready", message));
            for (int round = 0; round < 4; round++)
            {
                await Task.WhenAll(children.Select(child => SendAsync(child, "try", timeout.Token)));
                string?[] results = await Task.WhenAll(children.Select(child => child.StandardOutput.ReadLineAsync(timeout.Token).AsTask()));
                Assert.Single(results, static message => message == "locked");
                Assert.All(results, static message => Assert.True(message is "locked" or "busy"));
                Assert.True(FileLock.IsLocked(LockPath));
                Assert.False(TryLockAndUnlock(LockPath));
                Process winner = children[Array.IndexOf(results, "locked")];
                if (round == 0 && terminateFirstWinner)
                {
                    await KillAsync(winner);
                    children.Remove(winner);
                    winner.Dispose();
                }
                else
                {
                    await SendAsync(winner, "unlock", timeout.Token);
                    Assert.Equal("unlocked", await winner.StandardOutput.ReadLineAsync(timeout.Token));
                }

                Assert.False(FileLock.IsLocked(LockPath));
                Assert.True(TryLockAndUnlock(LockPath));
            }

            await Task.WhenAll(children.Select(child => SendAsync(child, "quit", timeout.Token)));
            await Task.WhenAll(children.Select(child => child.WaitForExitAsync(timeout.Token)));
            Assert.All(children, static child => Assert.Equal(0, child.ExitCode));
        }
        finally
        {
            try
            {
                await Task.WhenAll(children.Select(KillAsync));
            }
            finally
            {
                foreach (Process child in children)
                {
                    child.Dispose();
                }
            }
        }
    }

    [Fact]
    public void LongPathsCanBeLockedQueriedAndUnlocked()
    {
        string parent = _directory;
        for (int index = 0; index < 20; index++)
        {
            parent = Path.Combine(parent, "long-path-segment");
        }

        string path = Path.Combine(parent, "resource.lock");
        using (FileLock.LockScoped(path))
        {
            Assert.True(FileLock.IsLocked(path));
        }

        Assert.False(FileLock.IsLocked(path));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProcessExitReleasesNativeOwnership(bool terminate, bool disableRuntimeLocking)
    {
        using Process child = StartLockOwner(LockPath, disableRuntimeLocking);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            Assert.Equal("locked", await child.StandardOutput.ReadLineAsync(timeout.Token));
            var observer = new FileLock(LockPath);
            Assert.False(observer.OwnsLock);
            Assert.True(FileLock.IsLocked(LockPath));
            Assert.False(TryLockAndUnlock(LockPath));
            if (terminate)
            {
                child.Kill(entireProcessTree: true);
            }
            else
            {
                await child.StandardInput.WriteLineAsync("release".AsMemory(), timeout.Token);
                await child.StandardInput.FlushAsync(timeout.Token);
            }

            await child.WaitForExitAsync(timeout.Token);
            if (!terminate)
            {
                Assert.Equal(0, child.ExitCode);
            }

            Assert.True(File.Exists(LockPath));
            Assert.False(FileLock.IsLocked(LockPath));
            Assert.True(TryLockAndUnlock(LockPath));
        }
        finally
        {
            await KillAsync(child);
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string LockPath => Path.Combine(_directory, "resource.lock");

    private static void AcquireAndRelease(string path)
    {
        using FileLockScope scope = FileLock.LockScoped(path);
    }

    private static bool TryLockAndUnlock(string path)
    {
        var fileLock = new FileLock(path);
        try
        {
            return fileLock.TryLock();
        }
        finally
        {
            fileLock.Unlock();
        }
    }

    private static async Task SendAsync(Process child, string command, CancellationToken cancellationToken)
    {
        await child.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
        await child.StandardInput.FlushAsync(cancellationToken);
    }

    private static Process StartLockOwner(
        string path, bool disableRuntimeLocking, string command = "file-lock")
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (disableRuntimeLocking)
        {
            startInfo.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";
        }

        string[] arguments =
        [
            "exec",
            "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Incant.UnitTest.Base.runtimeconfig.json"),
            "--depsfile", Path.Combine(AppContext.BaseDirectory, "Incant.UnitTest.Base.deps.json"),
            Path.Combine(AppContext.BaseDirectory, "Incant.ProcessTestHelper.dll"),
            command, path
        ];
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("The file lock test process did not start.");
    }

    private static async Task KillAsync(Process child)
    {
        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (child.HasExited)
        {
            // The process may exit between the liveness query and termination.
        }

        // Cleanup must finish even when the test itself is cancelled.
        await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    private readonly string _directory;
}
