using Incant.Base;
using Incant.Base.Deps;

namespace Incant.UnitTest.Base.Deps;

public sealed class DatabaseLifecycleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OpeningAMissingDatabaseCreatesOnlyAHeader(bool tryOpen, bool missingParent)
    {
        using var directory = new DatabaseTestDirectory();
        string parent = missingParent ? Path.Combine(directory.Path, "new", "nested") : directory.Path;
        string path = Path.Combine(parent, "records.db");
        var database = new Database(path);
        try
        {
            Assert.False(File.Exists(path));
            if (tryOpen)
            {
                Assert.True(database.TryOpen());
            }
            else
            {
                database.Open();
            }

            Assert.True(database.IsOpened);
            Assert.Equal(0, database.Count);
            Assert.Equal(0, database.TotalRecordCount);
            Assert.True(database.IsOutdated("missing", null, null));
        }
        finally
        {
            database.Close();
        }

        string header = Assert.Single(File.ReadAllLines(path));
        Assert.StartsWith("@incant-deps,", header);
        byte[] original = File.ReadAllBytes(path);
        using (Database.OpenScoped(database))
        {
            Assert.Equal(0, database.Count);
            Assert.Equal(0, database.TotalRecordCount);
        }

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReopeningAfterFileDeletionCreatesAFreshEmptyDatabase()
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        using (Database.OpenScoped(database))
        {
            database.RunIfOutdated("old", _ => { }, null, null);
        }

        File.Delete(directory.DatabasePath);
        using (Database.OpenScoped(database))
        {
            Assert.Equal(0, database.Count);
            Assert.Equal(0, database.TotalRecordCount);
            Assert.True(database.IsOutdated("old", null, null));
        }

        Assert.Single(File.ReadAllLines(directory.DatabasePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructionAndClosingAnUnopenedDatabaseDoNotAccessFiles(bool readOnly)
    {
        using var directory = new DatabaseTestDirectory();
        string missingDirectory = Path.Combine(directory.Path, "missing");
        var database = new Database(Path.Combine(missingDirectory, "records.db"), readOnly: readOnly);
        Assert.False(database.IsOpened);
        Assert.Equal(readOnly, database.IsReadOnly);
        database.Close();
        database.Close();
        Assert.False(Directory.Exists(missingDirectory));

        // Filesystem-dependent validation belongs to opening, not construction.
        var directoryPath = new Database(directory.Path, readOnly: readOnly);
        Assert.False(directoryPath.IsOpened);
        Assert.Throws<IOException>(directoryPath.Open);
        Assert.False(directoryPath.IsOpened);
        Assert.Throws<IOException>(() => directoryPath.TryOpen());
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void ConstructionDoesNotRecoverCorruptContentsOrAcquireTheWriterLock()
    {
        using var directory = new DatabaseTestDirectory();
        File.WriteAllText(directory.DatabasePath, "unchanged corrupt data");
        using FileLockScope held = FileLock.LockScoped(directory.DatabasePath + ".lock");
        Database database = directory.Create();
        database.Close();
        Assert.False(database.IsOpened);
        Assert.Equal("unchanged corrupt data", File.ReadAllText(directory.DatabasePath));
        Assert.False(File.Exists(directory.DatabasePath + ".compact"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedDatabaseRejectsOperationsBeforeAndAfterOpening(bool readOnly)
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create(readOnly: readOnly);
        await AssertClosedAsync(database);
        using (Database.OpenScoped(database))
        {
            Assert.True(database.IsOpened);
        }

        await AssertClosedAsync(database);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedOpeningIsRejectedWithoutClosingTheCurrentSession(bool readOnly)
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create(readOnly: readOnly);
        using DatabaseScope scope = Database.OpenScoped(database);
        Assert.Throws<InvalidOperationException>(database.Open);
        Assert.Throws<InvalidOperationException>(() => database.TryOpen());
        Assert.Throws<InvalidOperationException>(() => Database.OpenScoped(database));
        Assert.True(database.IsOpened);
        Assert.True(database.IsOutdated("missing", null, null));
    }

    [Fact]
    public void WriterContentionLeavesTheSameInstanceRetryable()
    {
        using var directory = new DatabaseTestDirectory();
        Database owner = directory.Create();
        Database contender = directory.Create();
        using DatabaseScope ownerScope = Database.OpenScoped(owner);
        try
        {
            Assert.False(contender.TryOpen());
            Assert.Throws<IOException>(contender.Open);
            Assert.Throws<IOException>(() => Database.OpenScoped(contender));
            Assert.False(contender.IsOpened);
            Assert.True(owner.IsOpened);
            owner.RunIfOutdated("key", _ => { }, null, ["saved"]);
            owner.Close();

            Assert.True(contender.TryOpen());
            Assert.False(contender.IsOutdated("key", null, ["saved"]));
            contender.Close();
            owner.Open();
            Assert.True(owner.IsOpened);
        }
        finally
        {
            contender.Close();
            owner.Close();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitializationFailureReleasesTheWriterLockAndAllowsRetry(bool tryOpen)
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        string compactPath = directory.DatabasePath + ".compact";
        Directory.CreateDirectory(compactPath);
        try
        {
            Exception? failure = Xunit.Record.Exception(() =>
            {
                if (tryOpen)
                {
                    database.TryOpen();
                }
                else
                {
                    database.Open();
                }
            });
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.False(database.IsOpened);
            Assert.False(FileLock.IsLocked(directory.DatabasePath + ".lock"));
            database.Close();
            Directory.Delete(compactPath);

            Assert.True(database.TryOpen());
            Assert.Equal(0, database.Count);
            Assert.Equal(0, database.TotalRecordCount);
            Assert.True(database.RunIfOutdated("recovered", _ => { }, null, null));
        }
        finally
        {
            database.Close();
        }
    }

    [Fact]
    public void FailedReopenCanRecoverWithoutRestoringOldRecords()
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        using (Database.OpenScoped(database))
        {
            database.RunIfOutdated("old", _ => { }, null, null);
        }

        // Initializing a replacement header fails after the previous session has closed.
        File.WriteAllText(directory.DatabasePath, "corrupt");
        string compactPath = directory.DatabasePath + ".compact";
        Directory.CreateDirectory(compactPath);
        Exception? failure = Xunit.Record.Exception(database.Open);
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.False(database.IsOpened);
        Directory.Delete(compactPath);

        using DatabaseScope recovered = Database.OpenScoped(database);
        Assert.Equal(0, database.Count);
        Assert.Equal(0, database.TotalRecordCount);
        Assert.True(database.IsOutdated("old", null, null));
    }

    [Fact]
    public void ReopeningReloadsExternalChangesAndReleasesOldRecords()
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        using (Database.OpenScoped(database))
        {
            database.RunIfOutdated("removed", _ => { }, null, null);
            database.RunIfOutdated("retained", _ => { }, null, ["old"]);
        }

        Database other = directory.Create();
        using (Database.OpenScoped(other))
        {
            Assert.False(other.IsOutdated("retained", null, ["old"]));
            other.ClearDatabase();
            other.RunIfOutdated("retained", _ => { }, null, ["new"]);
        }

        using (Database.OpenScoped(database))
        {
            Assert.Equal(1, database.Count);
            Assert.Equal(1, database.TotalRecordCount);
            Assert.True(database.IsOutdated("removed", null, null));
            Assert.False(database.IsOutdated("retained", null, ["new"]));
        }

        using DatabaseScope reopened = Database.OpenScoped(database);
        Assert.Equal(1, database.TotalRecordCount);
    }

    [Fact]
    public void ReadOnlyReopenRefreshesTheSnapshotWithoutTakingTheWriterLock()
    {
        using var directory = new DatabaseTestDirectory();
        Database reader = directory.Create(readOnly: true);
        Database writer = directory.Create();
        using DatabaseScope initial = Database.OpenScoped(reader);
        Assert.True(reader.IsOpened);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));

        using DatabaseScope writerScope = Database.OpenScoped(writer);
        writer.RunIfOutdated("key", _ => { }, null, ["value"]);
        Assert.Equal(0, reader.Count);
        reader.Close();
        using (Database.OpenScoped(reader))
        {
            Assert.Equal(1, reader.Count);
            Assert.False(reader.IsOutdated("key", null, ["value"]));
            writer.ClearDatabase();
            Assert.Equal(1, reader.Count);
        }

        using DatabaseScope refreshed = Database.OpenScoped(reader);
        Assert.Equal(0, reader.Count);
        Assert.True(writer.IsOpened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultCopiedAndStaleScopesCannotCloseALaterOpening(bool readOnly)
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create(readOnly: readOnly);
        DatabaseScope empty = default;
        empty.Dispose();
        Assert.Throws<ArgumentNullException>(() => Database.OpenScoped(null!));
        using DatabaseScope first = Database.OpenScoped(database);
        DatabaseScope copy = first;
        first.Dispose();
        first.Dispose();
        Assert.False(database.IsOpened);

        using DatabaseScope next = Database.OpenScoped(database);
        copy.Dispose();
        first.Dispose();
        empty.Dispose();
        Assert.True(database.IsOpened);
        next.Dispose();
        Assert.False(database.IsOpened);
    }

    [Fact]
    public void ScopeClosesAfterCompactionAndExceptionUnwinding()
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        Action fail = () =>
        {
            using DatabaseScope scope = Database.OpenScoped(database);
            database.RunIfOutdated("key", _ => { }, null, ["before"]);
            database.Compact();
            database.ClearDatabase();
            database.RunIfOutdated("key", _ => { }, null, ["after"]);
            throw new BuildFailureException();
        };
        Assert.Throws<BuildFailureException>(fail);

        Assert.False(database.IsOpened);
        Assert.False(FileLock.IsLocked(directory.DatabasePath + ".lock"));
        using DatabaseScope reopened = Database.OpenScoped(database);
        Assert.False(database.IsOutdated("key", null, ["after"]));
    }

    [Fact]
    public async Task ConcurrentOpeningOnOneInstanceHasOnlyOneOwner()
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        try
        {
            Task<bool>[] attempts = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            {
                try
                {
                    return database.TryOpen();
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            })).ToArray();
            bool[] results = await Task.WhenAll(attempts);
            Assert.Single(results, static opened => opened);
            Assert.True(database.IsOpened);
            database.RunIfOutdated("key", _ => { }, null, null);
        }
        finally
        {
            database.Close();
        }

        using DatabaseScope reopened = Database.OpenScoped(database);
        Assert.False(database.IsOutdated("key", null, null));
    }

    private static async Task AssertClosedAsync(Database database)
    {
        Assert.False(database.IsOpened);
        Assert.Throws<InvalidOperationException>(() => database.Count);
        Assert.Throws<InvalidOperationException>(() => database.TotalRecordCount);
        Assert.Throws<InvalidOperationException>(() => database.IsOutdated("key", null, null));
        Assert.Throws<InvalidOperationException>(() => database.RunIfOutdated("key", _ => Assert.Fail(), null, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.RunIfOutdated("key", _ => Task.FromException(new BuildFailureException()), null, null));
        Assert.Throws<InvalidOperationException>(() => database.Flush());
        Assert.Throws<InvalidOperationException>(() => database.Compact());
        Assert.Throws<InvalidOperationException>(() => database.ClearDatabase());
    }

    private sealed class BuildFailureException : Exception;
}
