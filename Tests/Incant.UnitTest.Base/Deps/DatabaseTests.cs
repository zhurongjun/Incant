using System.Text;
using Incant.Base.Deps;

namespace Incant.UnitTest.Base.Deps;

public sealed class DatabaseTests
{
    [Fact]
    public void MissingReadOnlyDatabaseCreatesNothingAndRejectsWrites()
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create(readOnly: true);
        using DatabaseScope databaseScope = Database.OpenScoped(database);
        Assert.True(database.IsReadOnly);
        Assert.Equal(0, database.Count);
        Assert.True(database.IsOutdated("missing", null, null));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
        Assert.Throws<InvalidOperationException>(() => database.RunIfOutdated("key", _ => Assert.Fail(), null, null));
        Assert.Throws<InvalidOperationException>(() => database.ClearDatabase());
        Assert.Throws<InvalidOperationException>(() => database.Compact());
        Assert.Throws<InvalidOperationException>(() => database.Flush());
    }

    [Fact]
    public async Task ReadOnlySnapshotsRemainIndependentOfAppendCompactAndClear()
    {
        using var directory = new DatabaseTestDirectory();
        Database writer = directory.Create();
        using DatabaseScope writerScope = Database.OpenScoped(writer);
        writer.RunIfOutdated("first", _ => { }, null, ["old"]);
        Database snapshot = directory.Create(readOnly: true);
        using DatabaseScope snapshotScope = Database.OpenScoped(snapshot);
        writer.RunIfOutdated("first", _ => { }, null, ["new"]);
        writer.RunIfOutdated("second", _ => { }, null, null);
        writer.Compact();
        Assert.Equal(1, snapshot.Count);
        Assert.False(snapshot.IsOutdated("first", null, ["old"]));
        Assert.True(snapshot.IsOutdated("second", null, null));
        Database current = directory.Create(readOnly: true);
        using (Database.OpenScoped(current))
        {
            Assert.Equal(2, current.Count);
            Assert.False(current.IsOutdated("first", null, ["new"]));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            snapshot.RunIfOutdated("first", _ => Task.CompletedTask, null, null));
        writer.ClearDatabase();
        Assert.Equal(0, writer.Count);
        Assert.Equal(0, writer.TotalRecordCount);
        Assert.False(snapshot.IsOutdated("first", null, ["old"]));
        Database empty = directory.Create(readOnly: true);
        using DatabaseScope emptyScope = Database.OpenScoped(empty);
        Assert.Equal(0, empty.Count);
    }

    [Fact]
    public void WriterLockIsExclusiveAndClosingReleasesIt()
    {
        using var directory = new DatabaseTestDirectory();
        Database writer = directory.Create();
        using DatabaseScope writerScope = Database.OpenScoped(writer);
        writer.RunIfOutdated("stable", _ => { }, null, null);
        Assert.ThrowsAny<IOException>(() => directory.Create().Open());
        Database reader = directory.Create(readOnly: true);
        using (Database.OpenScoped(reader))
        {
            Assert.False(reader.IsOutdated("stable", null, null));
        }

        writer.Close();
        writer.Close();
        Assert.True(File.Exists(directory.DatabasePath + ".lock"));
        Assert.Throws<InvalidOperationException>(() => writer.IsOutdated("key", null, null));
        Assert.Throws<InvalidOperationException>(() => writer.RunIfOutdated("key", _ => { }, null, null));
        Assert.Throws<InvalidOperationException>(() => writer.Flush());
        Assert.Throws<InvalidOperationException>(() => writer.Compact());
        Assert.Throws<InvalidOperationException>(() => writer.ClearDatabase());
        Assert.Throws<InvalidOperationException>(() => writer.Count);
        Database next = directory.Create();
        using DatabaseScope nextScope = Database.OpenScoped(next);
        Assert.False(next.IsOutdated("stable", null, null));
    }

    [Fact]
    public void CompactionUsesStrictCountAndRatioThresholds()
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        using DatabaseScope databaseScope = Database.OpenScoped(database);
        var force = new CheckOptions { Force = true };
        for (int index = 0; index < 1000; index++)
        {
            database.RunIfOutdated("one", _ => { }, null, null, force);
        }

        Assert.Equal(1000, database.TotalRecordCount);
        database.RunIfOutdated("one", _ => { }, null, null, force);
        Assert.Equal(1, database.TotalRecordCount);
        database.ClearDatabase();
        for (int index = 0; index < 400; index++)
        {
            database.RunIfOutdated("key-" + index, _ => { }, null, null);
        }

        for (int index = 0; index < 800; index++)
        {
            database.RunIfOutdated("key-0", _ => { }, null, null, force);
        }

        Assert.Equal(1200, database.TotalRecordCount);
        database.RunIfOutdated("key-0", _ => { }, null, null, force);
        Assert.Equal(400, database.TotalRecordCount);
        Assert.Equal(400, database.Count);
        Assert.False(File.Exists(directory.DatabasePath + ".compact"));
    }

    [Fact]
    public void CompactionFailurePreservesCommittedAppendsAndAllowsRetry()
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        using DatabaseScope databaseScope = Database.OpenScoped(database);
        var force = new CheckOptions { Force = true };
        for (int index = 0; index < 1000; index++)
        {
            database.RunIfOutdated("key", _ => { }, null, ["before"], force);
        }

        string compactPath = directory.DatabasePath + ".compact";
        Directory.CreateDirectory(compactPath);
        Exception? failure = Xunit.Record.Exception(() =>
            database.RunIfOutdated("key", _ => { }, null, ["committed"]));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(1001, database.TotalRecordCount);
        Assert.False(database.IsOutdated("key", null, ["committed"]));
        Database reader = directory.Create(readOnly: true);
        using (Database.OpenScoped(reader))
        {
            Assert.False(reader.IsOutdated("key", null, ["committed"]));
        }

        Directory.Delete(compactPath);
        database.Compact();
        Assert.Equal(1, database.TotalRecordCount);
        Assert.False(database.IsOutdated("key", null, ["committed"]));
    }

    [Fact]
    public void InvalidTextDoesNotAppendAndTheWriterCanRecover()
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        using (Database.OpenScoped(database))
        {
            database.RunIfOutdated("key", _ => { }, null, ["stable"]);
            long length = new FileInfo(directory.DatabasePath).Length;
            bool executed = false;
            Assert.Throws<EncoderFallbackException>(() =>
                database.RunIfOutdated("key", _ => executed = true, null, ["\uD800"]));
            Assert.True(executed);
            Assert.Equal(length, new FileInfo(directory.DatabasePath).Length);
            Assert.False(database.IsOutdated("key", null, ["stable"]));
            Assert.True(database.RunIfOutdated("key", _ => { }, null, ["changed"]));
        }

        Database reopened = directory.Create(readOnly: true);
        using DatabaseScope reopenedScope = Database.OpenScoped(reopened);
        Assert.False(reopened.IsOutdated("key", null, ["changed"]));
    }

    [Fact]
    public async Task ParallelKeysAndOverlappingSameKeyCallbacksRemainComplete()
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        using DatabaseScope databaseScope = Database.OpenScoped(database);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int index = 0; index < 128; index++)
            {
                database.RunIfOutdated($"{worker}-{index}", _ => { }, null, ["value"]);
            }
        })));
        Assert.Equal(1024, database.Count);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> delayed = database.RunIfOutdated("shared", async _ =>
        {
            started.SetResult();
            await release.Task;
        }, null, ["last"]);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(database.RunIfOutdated("shared", _ => { }, null, ["first"]));
        }
        finally
        {
            // Finish the callback before closing the database, even when the assertion or test is cancelled.
            release.TrySetResult();
            await delayed.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        Assert.True(await delayed);
        Assert.False(database.IsOutdated("shared", null, ["last"]));
        Database snapshot = directory.Create(readOnly: true);
        using DatabaseScope snapshotScope = Database.OpenScoped(snapshot);
        Assert.Equal(1025, snapshot.Count);
        for (int worker = 0; worker < 8; worker++)
        {
            for (int index = 0; index < 128; index++)
            {
                Assert.False(snapshot.IsOutdated($"{worker}-{index}", null, ["value"]));
            }
        }
    }

    [Fact]
    public void DefaultShaAndExplicitFlushWorkAcrossReopen()
    {
        using var directory = new DatabaseTestDirectory();
        string input = directory.CreateFile("input.txt", "first");
        DateTime timestamp = File.GetLastWriteTimeUtc(input);
        var database = new Database(directory.DatabasePath, defaultUseSHA: true);
        using (Database.OpenScoped(database))
        {
            database.RunIfOutdated("key", _ => { }, [input], null);
            database.Flush(false);
            database.Flush(true);
        }

        File.WriteAllText(input, "other");
        File.SetLastWriteTimeUtc(input, timestamp);
        var reopened = new Database(directory.DatabasePath, defaultUseSHA: true);
        using DatabaseScope reopenedScope = Database.OpenScoped(reopened);
        Assert.True(reopened.IsOutdated("key", [input], null));
    }

    [Fact]
    public void DatabaseUsesTheSuppliedPathAndRejectsInvalidPaths()
    {
        using var directory = new DatabaseTestDirectory();
        string path = System.IO.Path.Combine(directory.Path, "dependency cache");
        var database = new Database(path);
        using (Database.OpenScoped(database))
        {
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".db"));
        }

        Assert.Throws<ArgumentNullException>(() => new Database(null!));
        Assert.Throws<ArgumentException>(() => new Database(""));
        Assert.Throws<ArgumentException>(() => new Database(" "));
        Assert.Throws<ArgumentException>(() => new Database("\0"));
    }

    [Fact]
    public void ClearingOneDatabaseRefreshesTheSharedCacheWithoutClearingOtherRecords()
    {
        using var firstDirectory = new DatabaseTestDirectory();
        using var secondDirectory = new DatabaseTestDirectory();
        string input = firstDirectory.CreateFile("input.txt");
        var cache = new FileSnapshotCache();
        Database first = firstDirectory.Create(cache: cache);
        using DatabaseScope firstScope = Database.OpenScoped(first);
        Database second = secondDirectory.Create(cache: cache);
        using DatabaseScope secondScope = Database.OpenScoped(second);
        first.RunIfOutdated("key", _ => { }, [input], null);
        second.RunIfOutdated("key", _ => { }, [input], null);
        File.SetLastWriteTimeUtc(input, File.GetLastWriteTimeUtc(input).AddMinutes(1));
        Assert.False(second.IsOutdated("key", [input], null));
        first.ClearDatabase();
        Assert.Equal(1, second.Count);
        Assert.True(second.IsOutdated("key", [input], null));
    }
}
