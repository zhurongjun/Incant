using Incant.Base.Deps;
using Record = Incant.Base.Deps.Record;

namespace Incant.UnitTest.Base.Deps;

public sealed class DatabaseBehaviorTests
{
    [Fact]
    public void ReopenPreservesCanonicalKeysFileSetsAndOrderedArgs()
    {
        using var directory = new DatabaseTestDirectory();
        string first = directory.CreateFile("first.txt");
        string second = directory.CreateFile("second.txt");
        Database database = directory.Create();
        using (Database.OpenScoped(database))
        {
            Assert.True(database.RunIfOutdated("Compile", _ => { }, [second, first, first], ["a", "a", "b"]));
        }

        Database reopened = directory.Create();
        using DatabaseScope reopenedScope = Database.OpenScoped(reopened);
        Assert.False(reopened.RunIfOutdated("COMPILE", _ => Assert.Fail("Current records must not run."),
            [first, second], ["a", "a", "b"]));
        Assert.True(reopened.RunIfOutdated("compile", _ => { }, [first, second], ["a", "b", "a"]));
        Assert.True(reopened.RunIfOutdated("compile", _ => { }, [first], ["a", "b", "a"]));
        Assert.True(reopened.RunIfOutdated("compile", _ => { }, [first, second], ["a", "b", "a"]));
    }

    [Fact]
    public void ScalarAndDelimiterValuesRoundTrip()
    {
        using var directory = new DatabaseTestDirectory();
        string input = directory.CreateFile("input,中文.txt");
        string external = directory.CreateFile("output,一.txt");
        string allControls = string.Concat(Enumerable.Range(0, 32).Select(static value => (char)value));
        string[] args = ["", "comma,\"quote\"", @"slash\n\path\", "中文🚀", allControls,
            "first\r\n@record,1,fake,fake\nlast", new string('x', 80_000)];
        Database database = directory.Create();
        using (Database.OpenScoped(database))
        {
            Assert.True(database.RunIfOutdated("escape", record => record.AddExternalFileRange([external, external]), [input], args));
        }

        Database reopened = directory.Create();
        using DatabaseScope reopenedScope = Database.OpenScoped(reopened);
        Assert.False(reopened.RunIfOutdated("escape", _ => Assert.Fail("Round-trip changed record contents."), [input], args));
    }

    [Fact]
    public void TimestampShaAndModeChangesInvalidateTheRecord()
    {
        using var directory = new DatabaseTestDirectory();
        string path = directory.CreateFile("input.txt", "before");
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        Database database = directory.Create();
        using DatabaseScope databaseScope = Database.OpenScoped(database);
        Assert.True(database.RunIfOutdated("compile", _ => { }, [path], null));
        File.WriteAllText(path, "after!");
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.False(database.RunIfOutdated("compile", _ => { }, [path], null));

        var sha = new CheckOptions { UseSHA = true };
        Assert.True(database.RunIfOutdated("compile", _ => { }, [path], null, sha));
        Assert.False(database.RunIfOutdated("compile", _ => { }, [path], null, sha));
        File.WriteAllText(path, "third!");
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.True(database.RunIfOutdated("compile", _ => { }, [path], null, sha));
        Assert.True(database.RunIfOutdated("compile", _ => { }, [path], null));
        File.SetLastWriteTimeUtc(path, timestamp.AddMinutes(1));
        Assert.True(database.RunIfOutdated("compile", _ => { }, [path], null));
        File.Delete(path);
        Assert.True(database.RunIfOutdated("compile", _ => { }, [path], null));
        Assert.True(database.RunIfOutdated("compile", _ => { }, [path], null));
        File.WriteAllText(path, "returned");
        Assert.True(database.RunIfOutdated("compile", _ => { }, [path], null));
        Assert.False(database.RunIfOutdated("compile", _ => { }, [path], null));
    }

    [Fact]
    public async Task AsyncCallbacksCompleteBeforePersistenceAndFailuresKeepOldRecords()
    {
        using var directory = new DatabaseTestDirectory();
        string external = System.IO.Path.Combine(directory.Path, "external.txt");
        Database database = directory.Create();
        using DatabaseScope databaseScope = Database.OpenScoped(database);
        Assert.True(await database.RunIfOutdated("generate", async record =>
        {
            await Task.Yield();
            File.WriteAllText(external, "complete");
            record.AddExternalFile(external);
        }, files: null, args: ["stable"]));

        await Assert.ThrowsAsync<BuildFailureException>(() => database.RunIfOutdated("generate", async _ =>
        {
            await Task.Yield();
            throw new BuildFailureException();
        }, files: null, args: ["different"]));
        Action<Record> failAction = _ => throw new BuildFailureException();
        Assert.Throws<BuildFailureException>(() =>
            database.RunIfOutdated("generate", failAction, files: null, args: ["different"]));
        Assert.False(database.RunIfOutdated("generate",
            _ => Assert.Fail("A failed callback replaced a valid record."), files: null, args: ["stable"]));
        File.Delete(external);
        Assert.True(database.RunIfOutdated("generate", _ => { }, files: null, args: ["stable"]));
    }

    [Fact]
    public void ForceCacheAndClearRetainTheirOriginalContracts()
    {
        using var directory = new DatabaseTestDirectory();
        string input = directory.CreateFile("input.txt");
        var cache = new FileSnapshotCache();
        Database database = directory.Create(cache: cache);
        using DatabaseScope databaseScope = Database.OpenScoped(database);
        Assert.True(database.RunIfOutdated("compile", _ => { }, [input], null));
        File.SetLastWriteTimeUtc(input, File.GetLastWriteTimeUtc(input).AddMinutes(1));
        Assert.False(database.RunIfOutdated("compile", _ => { }, [input], null));
        cache.Clear();
        Assert.True(database.RunIfOutdated("compile", _ => { }, [input], null));
        Assert.True(database.RunIfOutdated("compile", _ => { }, [input], null, new CheckOptions { Force = true }));
        database.ClearDatabase();
        Assert.True(database.RunIfOutdated("compile", _ => { }, [input], null));
        Assert.False(database.RunIfOutdated("compile", _ => { }, [input], null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InputsAreCapturedBeforeTheActionAndOutputsAfterIt(bool useSHA)
    {
        using var directory = new DatabaseTestDirectory();
        string input = directory.CreateFile("input.txt", "before");
        string output = System.IO.Path.Combine(directory.Path, "output.txt");
        DateTime originalTimestamp = File.GetLastWriteTimeUtc(input);
        var args = new List<string> { "original" };
        var options = new CheckOptions { UseSHA = useSHA };
        Database database = directory.Create();
        using DatabaseScope scope = Database.OpenScoped(database);

        Assert.True(database.RunIfOutdated("compile", record =>
        {
            File.WriteAllText(input, "after");
            File.SetLastWriteTimeUtc(input, originalTimestamp.AddMinutes(1));
            File.WriteAllText(output, "generated");
            record.AddExternalFile(output);
            args[0] = "changed";
        }, [input], args, options));
        Assert.True(database.IsOutdated("compile", [input], ["original"], options));

        File.WriteAllText(input, "before");
        File.SetLastWriteTimeUtc(input, originalTimestamp);
        Assert.False(database.IsOutdated("compile", [input], ["original"], options));
        Assert.True(database.IsOutdated("compile", [input], args, options));
        database.Compact();

        Database reader = directory.Create(readOnly: true);
        using DatabaseScope readerScope = Database.OpenScoped(reader);
        Assert.False(reader.IsOutdated("compile", [input], ["original"], options));
        File.Delete(output);
        Assert.True(reader.IsOutdated("compile", [input], ["original"], options));
    }

    [Fact]
    public void RetainingCallbackRecordCannotChangePersistedSnapshot()
    {
        using var directory = new DatabaseTestDirectory();
        Record retained = new();
        Database database = directory.Create();
        using DatabaseScope databaseScope = Database.OpenScoped(database);
        Assert.True(database.RunIfOutdated("compile", record => retained = record, null, null));
        retained.AddExternalFile(System.IO.Path.Combine(directory.Path, "not-created"));
        Assert.False(database.RunIfOutdated("compile",
            _ => Assert.Fail("A retained callback changed saved data."), null, null));
        database.Compact();
        Database reader = directory.Create(readOnly: true);
        using DatabaseScope readerScope = Database.OpenScoped(reader);
        Assert.False(reader.IsOutdated("compile", null, null));
    }

    [Fact]
    public void InvalidInputsDoNotRunCallbacksAndKeysRemainReusable()
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        using DatabaseScope databaseScope = Database.OpenScoped(database);
        string?[] invalidKeys = [null, "", " ", ".", "..", "a/b", "a\\b", "CON", "aux.txt", "name.", "name ", "a\nb"];
        foreach (string? key in invalidKeys)
        {
            Assert.ThrowsAny<ArgumentException>(() =>
                database.RunIfOutdated(key!, _ => Assert.Fail("An invalid key ran."), null, null));
        }

        Assert.Throws<ArgumentException>(() => database.RunIfOutdated("valid", _ => Assert.Fail(), files: [null!], args: null));
        Assert.Throws<ArgumentException>(() => database.RunIfOutdated("valid", _ => Assert.Fail(), files: [""], args: null));
        Assert.Throws<ArgumentException>(() => database.RunIfOutdated("valid", _ => Assert.Fail(), files: null, args: [null!]));
        Assert.True(database.RunIfOutdated("valid", _ => { }, null, null));
        Assert.False(database.RunIfOutdated("VALID", _ => { }, null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistedExternalFilesDetectChangesInBothComparisonModes(bool useSHA)
    {
        using var directory = new DatabaseTestDirectory();
        string output = System.IO.Path.Combine(directory.Path, "output.txt");
        var options = new CheckOptions { UseSHA = useSHA };
        Database database = directory.Create();
        using (Database.OpenScoped(database))
        {
            Assert.True(database.RunIfOutdated("generate", record =>
            {
                File.WriteAllText(output, "generated");
                record.AddExternalFile(output);
            }, null, null, options));
        }

        Database reopened = directory.Create();
        using DatabaseScope reopenedScope = Database.OpenScoped(reopened);
        Assert.False(reopened.RunIfOutdated("generate", _ => Assert.Fail(), null, null, options));
        DateTime timestamp = File.GetLastWriteTimeUtc(output);
        File.WriteAllText(output, "modified");
        File.SetLastWriteTimeUtc(output, useSHA ? timestamp : timestamp.AddMinutes(1));
        Assert.True(reopened.RunIfOutdated("generate", record => record.AddExternalFile(output), null, null, options));
        Assert.False(reopened.RunIfOutdated("generate", _ => Assert.Fail(), null, null, options));
        File.Delete(output);
        Assert.True(reopened.RunIfOutdated("generate", record =>
        {
            File.WriteAllText(output, "regenerated");
            record.AddExternalFileRange([output]);
        }, null, null, options));
        Assert.False(reopened.RunIfOutdated("generate", _ => Assert.Fail(), null, null, options));
    }

    [Fact]
    public void SharedCacheCanRepresentAMissingFileAcrossReopen()
    {
        using var directory = new DatabaseTestDirectory();
        string missing = System.IO.Path.Combine(directory.Path, "missing.txt");
        var cache = new FileSnapshotCache();
        int calls = 0;
        Database first = directory.Create(cache: cache);
        using (Database.OpenScoped(first))
        {
            Assert.True(first.RunIfOutdated("compile", _ => calls++, [missing], null));
        }

        Database reopened = directory.Create(cache: cache);
        using DatabaseScope reopenedScope = Database.OpenScoped(reopened);
        Assert.True(reopened.RunIfOutdated("compile", _ => calls++, [missing], null));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void SharedCacheSupportsSwitchingValidationModesAcrossReopen()
    {
        using var directory = new DatabaseTestDirectory();
        string input = directory.CreateFile("input.txt");
        var cache = new FileSnapshotCache();
        (bool UseSHA, bool Changed)[] checks =
        [
            (false, true), (true, true), (true, false), (false, true), (false, false)
        ];
        foreach ((bool useSHA, bool changed) in checks)
        {
            Database database = directory.Create(cache: cache);
            using DatabaseScope databaseScope = Database.OpenScoped(database);
            var options = new CheckOptions { UseSHA = useSHA };
            Assert.Equal(changed, database.RunIfOutdated("compile", _ => { }, [input], null, options));
        }
    }

    private sealed class BuildFailureException : Exception;
}
