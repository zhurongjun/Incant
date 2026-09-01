using Incant.Base;

namespace Incant.UnitTest.Base;

public sealed class DependencyDatabaseTests
{
    [Fact]
    public void FirstRunPersistsCanonicalRecordAndNextInstanceSkips()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        int invocationCount = 0;
        var firstDatabase = CreateDatabase(temporaryDirectory);

        bool firstResult = firstDatabase.RunIfOutdated(
            "Compile.Source",
            _ => invocationCount++,
            files: null,
            args: null);

        Assert.True(firstResult);
        Assert.Equal(1, invocationCount);
        string recordPath = GetRecordPath(temporaryDirectory, "compile.source");
        Assert.True(File.Exists(recordPath));
        Assert.DoesNotContain("version,", File.ReadAllText(recordPath));

        var secondDatabase = CreateDatabase(temporaryDirectory);
        bool secondResult = secondDatabase.RunIfOutdated(
            "COMPILE.SOURCE",
            _ => invocationCount++,
            files: null,
            args: null);

        Assert.False(secondResult);
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public void InstanceCanReuseAKeyRegardlessOfCasing()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var database = CreateDatabase(temporaryDirectory);

        Assert.True(database.RunIfOutdated("Asset", _ => { }, files: null, args: null));
        Assert.False(database.RunIfOutdated("ASSET", _ => { }, files: null, args: null));
    }

    [Fact]
    public void CustomBackendPersistsAndClearsDependencyRecords()
    {
        var backend = new MemoryDependencyDatabaseBackend();
        var database = new DependencyDatabase(backend);

        Assert.True(database.RunIfOutdated("Compile", _ => { }, files: null, args: null));
        Assert.False(database.RunIfOutdated("COMPILE", _ => { }, files: null, args: null));

        database.ClearDatabase();

        Assert.True(database.RunIfOutdated("compile", _ => { }, files: null, args: null));
    }

    [Fact]
    public void NullCustomBackendIsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DependencyDatabase((IDependencyDatabaseBackend)null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("folder/file")]
    [InlineData("folder\\file")]
    [InlineData("name:")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("line\nbreak")]
    public void InvalidKeysAreRejected(string? key)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var database = CreateDatabase(temporaryDirectory);

        Assert.ThrowsAny<ArgumentException>(
            () => database.RunIfOutdated(key!, _ => { }, files: null, args: null));
    }

    [Fact]
    public void InvalidInputCollectionsAreRejectedWithoutPersistingARecord()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var database = CreateDatabase(temporaryDirectory);

        Assert.Throws<ArgumentException>(
            () => database.RunIfOutdated("compile", _ => { }, [null!], args: null));
        Assert.Throws<ArgumentException>(
            () => database.RunIfOutdated("compile", _ => { }, files: null, [null!]));

        Assert.False(File.Exists(GetRecordPath(temporaryDirectory, "compile")));
    }

    [Fact]
    public void FileInputsAreUnorderedAndDeduplicatedWhileArgumentsRemainOrdered()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string firstInput = temporaryDirectory.CreateFile("first.txt", "first");
        string secondInput = temporaryDirectory.CreateFile("second.txt", "second");

        var firstDatabase = CreateDatabase(temporaryDirectory);
        Assert.True(
            firstDatabase.RunIfOutdated(
                "compile",
                _ => { },
                [secondInput, firstInput, firstInput],
                ["one", "two"]));

        var reorderedFileDatabase = CreateDatabase(temporaryDirectory);
        Assert.False(
            reorderedFileDatabase.RunIfOutdated(
                "compile",
                _ => { },
                [firstInput, secondInput],
                ["one", "two"]));

        var reorderedArgumentDatabase = CreateDatabase(temporaryDirectory);
        Assert.True(
            reorderedArgumentDatabase.RunIfOutdated(
                "compile",
                _ => { },
                [firstInput, secondInput],
                ["two", "one"]));
    }

    [Fact]
    public void ChangedInputTimestampInvalidatesTheRecord()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string inputPath = temporaryDirectory.CreateFile("input.txt", "before");
        var firstDatabase = CreateDatabase(temporaryDirectory);
        firstDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null);

        WriteWithNewTimestamp(inputPath, "after");

        int invocationCount = 0;
        var secondDatabase = CreateDatabase(temporaryDirectory);
        bool result = secondDatabase.RunIfOutdated(
            "compile",
            _ => invocationCount++,
            [inputPath],
            args: null);

        Assert.True(result);
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public void DeletedInputInvalidatesTheRecord()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string inputPath = temporaryDirectory.CreateFile("input.txt", "content");
        var firstDatabase = CreateDatabase(temporaryDirectory);
        firstDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null);
        File.Delete(inputPath);

        var secondDatabase = CreateDatabase(temporaryDirectory);

        Assert.True(
            secondDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null));
    }

    [Fact]
    public void ExternalFilesCanBeAddedAndAreValidatedOnLaterRuns()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputPath = Path.Combine(temporaryDirectory.Path, "output.txt");
        string manifestPath = Path.Combine(temporaryDirectory.Path, "manifest.txt");
        int invocationCount = 0;

        void Build(DependencyRecord record)
        {
            invocationCount++;
            File.WriteAllText(outputPath, $"build {invocationCount}");
            File.WriteAllText(manifestPath, $"manifest {invocationCount}");
            record.AddExternalFileRange([outputPath, manifestPath]);
        }

        var firstDatabase = CreateDatabase(temporaryDirectory);
        Assert.True(firstDatabase.RunIfOutdated("link", Build, files: null, args: null));

        var currentDatabase = CreateDatabase(temporaryDirectory);
        Assert.False(currentDatabase.RunIfOutdated("link", Build, files: null, args: null));

        WriteWithNewTimestamp(outputPath, "modified");
        var changedDatabase = CreateDatabase(temporaryDirectory);
        Assert.True(changedDatabase.RunIfOutdated("link", Build, files: null, args: null));

        File.Delete(manifestPath);
        var deletedDatabase = CreateDatabase(temporaryDirectory);
        Assert.True(deletedDatabase.RunIfOutdated("link", Build, files: null, args: null));
        Assert.Equal(3, invocationCount);
    }

    [Fact]
    public void ExternalFileAddMethodsRejectInvalidPaths()
    {
        var record = new DependencyRecord();

        Assert.Throws<ArgumentNullException>(() => record.AddExternalFile(null!));
        Assert.Throws<ArgumentException>(() => record.AddExternalFile(string.Empty));
        Assert.Throws<ArgumentNullException>(() => record.AddExternalFileRange(null!));
        Assert.Throws<ArgumentNullException>(() => record.AddExternalFileRange([null!]));
        Assert.Throws<ArgumentException>(() => record.AddExternalFileRange([string.Empty]));
    }

    [Fact]
    public void ShaModeDetectsContentChangesWhenTimestampIsRestored()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string inputPath = temporaryDirectory.CreateFile("input.txt", "first");
        DateTime originalTimestamp = File.GetLastWriteTimeUtc(inputPath);
        var options = new DependencyCheckOptions { UseSHA = true };
        var firstDatabase = CreateDatabase(temporaryDirectory);
        firstDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null, options);

        File.WriteAllText(inputPath, "other");
        File.SetLastWriteTimeUtc(inputPath, originalTimestamp);
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(inputPath));

        var secondDatabase = CreateDatabase(temporaryDirectory);

        Assert.True(
            secondDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null, options));
    }

    [Fact]
    public void MalformedPersistedShaDigestsAreTreatedAsOutdated()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string inputPath = temporaryDirectory.CreateFile("input.txt", "content");
        var options = new DependencyCheckOptions { UseSHA = true };
        string[] invalidDigests = ["00", new string('g', 64)];

        for (int index = 0; index < invalidDigests.Length; index++)
        {
            string key = $"compile-{index}";
            var initialDatabase = CreateDatabase(temporaryDirectory);
            initialDatabase.RunIfOutdated(key, _ => { }, [inputPath], args: null, options);
            ReplacePersistedSha(GetRecordPath(temporaryDirectory, key), invalidDigests[index]);

            var recoveryDatabase = CreateDatabase(temporaryDirectory);
            Assert.True(
                recoveryDatabase.RunIfOutdated(key, _ => { }, [inputPath], args: null, options));

            var verificationDatabase = CreateDatabase(temporaryDirectory);
            Assert.False(
                verificationDatabase.RunIfOutdated(key, _ => { }, [inputPath], args: null, options));
        }
    }

    [Fact]
    public void TimestampRecordContainingShaDigestIsTreatedAsOutdated()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string inputPath = temporaryDirectory.CreateFile("input.txt", "content");
        var initialDatabase = CreateDatabase(temporaryDirectory);
        initialDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null);
        string recordPath = GetRecordPath(temporaryDirectory, "compile");
        ReplacePersistedSha(recordPath, new string('0', 64));

        var recoveryDatabase = CreateDatabase(temporaryDirectory);

        Assert.True(
            recoveryDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null));
    }

    [Fact]
    public void ChangingValidationModeInvalidatesTheRecord()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string inputPath = temporaryDirectory.CreateFile("input.txt", "content");
        var timestampDatabase = CreateDatabase(temporaryDirectory);
        timestampDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null);

        var shaOptions = new DependencyCheckOptions { UseSHA = true };
        var shaDatabase = CreateDatabase(temporaryDirectory);
        Assert.True(
            shaDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null, shaOptions));

        var currentShaDatabase = CreateDatabase(temporaryDirectory);
        Assert.False(
            currentShaDatabase.RunIfOutdated(
                "compile",
                _ => { },
                [inputPath],
                args: null,
                shaOptions));
    }

    [Fact]
    public void DatabaseDefaultCanEnableShaValidation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string inputPath = temporaryDirectory.CreateFile("input.txt", "first");
        DateTime originalTimestamp = File.GetLastWriteTimeUtc(inputPath);
        var firstDatabase = CreateDatabase(temporaryDirectory, defaultUseSHA: true);
        firstDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null);

        File.WriteAllText(inputPath, "other");
        File.SetLastWriteTimeUtc(inputPath, originalTimestamp);

        var secondDatabase = CreateDatabase(temporaryDirectory, defaultUseSHA: true);

        Assert.True(
            secondDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null));
    }

    [Fact]
    public void ForceRunsAnUnchangedDependency()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var firstDatabase = CreateDatabase(temporaryDirectory);
        firstDatabase.RunIfOutdated("compile", _ => { }, files: null, args: null);
        int invocationCount = 0;
        var forceOptions = new DependencyCheckOptions { Force = true };
        var secondDatabase = CreateDatabase(temporaryDirectory);

        bool result = secondDatabase.RunIfOutdated(
            "compile",
            _ => invocationCount++,
            files: null,
            args: null,
            forceOptions);

        Assert.True(result);
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task AsyncActionPersistsExternalFilesAsync()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputPath = Path.Combine(temporaryDirectory.Path, "async-output.txt");
        var firstDatabase = CreateDatabase(temporaryDirectory);

        bool firstResult = await firstDatabase.RunIfOutdated(
            "generate",
            async record =>
            {
                await Task.Yield();
                File.WriteAllText(outputPath, "generated");
                record.AddExternalFile(outputPath);
            },
            files: null,
            args: null);

        var secondDatabase = CreateDatabase(temporaryDirectory);
        bool secondResult = secondDatabase.RunIfOutdated(
            "generate",
            _ => { },
            files: null,
            args: null);

        Assert.True(firstResult);
        Assert.False(secondResult);
    }

    [Fact]
    public void FailedActionLeavesTheOldRecordUnchanged()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var initialDatabase = CreateDatabase(temporaryDirectory);
        initialDatabase.RunIfOutdated("compile", _ => { }, files: null, ["stable"]);

        var failingDatabase = CreateDatabase(temporaryDirectory);
        Assert.Throws<TestBuildException>(
            () => failingDatabase.RunIfOutdated(
                "compile",
                (Action<DependencyRecord>)(_ => throw new TestBuildException()),
                files: null,
                ["changed"]));
        Assert.False(
            failingDatabase.RunIfOutdated("COMPILE", _ => { }, files: null, ["stable"]));

        var verificationDatabase = CreateDatabase(temporaryDirectory);
        Assert.False(
            verificationDatabase.RunIfOutdated("compile", _ => { }, files: null, ["stable"]));
    }

    [Fact]
    public void CsvRoundTripsQuotedAndMultilineValues()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string inputPath = temporaryDirectory.CreateFile("input,one.txt", "input");
        string outputPath = temporaryDirectory.CreateFile("output,two.txt", "output");
        string argument = "first,\"quoted\"\r\nsecond\nthird";
        var firstDatabase = CreateDatabase(temporaryDirectory);
        firstDatabase.RunIfOutdated(
            "escape",
            record => record.AddExternalFile(outputPath),
            [inputPath],
            [string.Empty, argument]);

        var secondDatabase = CreateDatabase(temporaryDirectory);

        Assert.False(
            secondDatabase.RunIfOutdated(
                "escape",
                _ => { },
                [inputPath],
                [string.Empty, argument]));
    }

    [Fact]
    public void CorruptRecordIsTreatedAsOutdatedAndReplaced()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var firstDatabase = CreateDatabase(temporaryDirectory);
        firstDatabase.RunIfOutdated("compile", _ => { }, files: null, args: null);
        string recordPath = GetRecordPath(temporaryDirectory, "compile");
        File.WriteAllText(recordPath, "key,compile\n\"unterminated");

        int invocationCount = 0;
        var recoveryDatabase = CreateDatabase(temporaryDirectory);
        Assert.True(
            recoveryDatabase.RunIfOutdated(
                "compile",
                _ => invocationCount++,
                files: null,
                args: null));

        var verificationDatabase = CreateDatabase(temporaryDirectory);
        Assert.False(
            verificationDatabase.RunIfOutdated("compile", _ => { }, files: null, args: null));
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public void RecordContainingVersionRowIsTreatedAsOutdated()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var initialDatabase = CreateDatabase(temporaryDirectory);
        initialDatabase.RunIfOutdated("compile", _ => { }, files: null, args: null);
        string recordPath = GetRecordPath(temporaryDirectory, "compile");
        string content = File.ReadAllText(recordPath);
        File.WriteAllText(recordPath, "version,1\n" + content);

        var recoveryDatabase = CreateDatabase(temporaryDirectory);

        Assert.True(
            recoveryDatabase.RunIfOutdated("compile", _ => { }, files: null, args: null));
    }

    [Fact]
    public void ClearingDatabaseRemovesRecordsAndAllowsKeyReuse()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var database = CreateDatabase(temporaryDirectory);
        database.RunIfOutdated("compile", _ => { }, files: null, args: null);
        string recordPath = GetRecordPath(temporaryDirectory, "compile");
        Assert.True(File.Exists(recordPath));

        database.ClearDatabase();

        Assert.False(File.Exists(recordPath));
        Assert.True(Directory.Exists(GetDatabasePath(temporaryDirectory)));
        Assert.True(database.RunIfOutdated("compile", _ => { }, files: null, args: null));
        Assert.True(File.Exists(recordPath));

        var newDatabase = CreateDatabase(temporaryDirectory);
        Assert.False(newDatabase.RunIfOutdated("compile", _ => { }, files: null, args: null));
    }

    [Fact]
    public void ClearingSharedCacheMakesFileChangesVisible()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string inputPath = temporaryDirectory.CreateFile("input.txt", "before");
        var cache = new DependencyDatabaseCache();
        var firstDatabase = CreateDatabase(temporaryDirectory, cache);
        firstDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null);
        WriteWithNewTimestamp(inputPath, "after");

        cache.Clear();
        var secondDatabase = CreateDatabase(temporaryDirectory, cache);

        Assert.True(
            secondDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null));
    }

    [Fact]
    public void SharedCacheCanRepresentAMissingFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string missingPath = Path.Combine(temporaryDirectory.Path, "missing.txt");
        var cache = new DependencyDatabaseCache();
        int invocationCount = 0;
        var firstDatabase = CreateDatabase(temporaryDirectory, cache);
        Assert.True(
            firstDatabase.RunIfOutdated(
                "compile",
                _ => invocationCount++,
                [missingPath],
                args: null));

        var secondDatabase = CreateDatabase(temporaryDirectory, cache);

        Assert.True(
            secondDatabase.RunIfOutdated(
                "compile",
                _ => invocationCount++,
                [missingPath],
                args: null));
        Assert.Equal(2, invocationCount);
    }

    [Fact]
    public void SharedCacheSupportsSwitchingValidationModes()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string inputPath = temporaryDirectory.CreateFile("input.txt", "content");
        var cache = new DependencyDatabaseCache();
        var timestampDatabase = CreateDatabase(temporaryDirectory, cache);
        timestampDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null);

        var shaOptions = new DependencyCheckOptions { UseSHA = true };
        var shaDatabase = CreateDatabase(temporaryDirectory, cache);
        Assert.True(
            shaDatabase.RunIfOutdated(
                "compile",
                _ => { },
                [inputPath],
                args: null,
                shaOptions));

        var currentShaDatabase = CreateDatabase(temporaryDirectory, cache);
        Assert.False(
            currentShaDatabase.RunIfOutdated(
                "compile",
                _ => { },
                [inputPath],
                args: null,
                shaOptions));

        var timestampAgainDatabase = CreateDatabase(temporaryDirectory, cache);
        Assert.True(
            timestampAgainDatabase.RunIfOutdated(
                "compile",
                _ => { },
                [inputPath],
                args: null));

        var currentTimestampDatabase = CreateDatabase(temporaryDirectory, cache);
        Assert.False(
            currentTimestampDatabase.RunIfOutdated(
                "compile",
                _ => { },
                [inputPath],
                args: null));
    }

    [Fact]
    public void ClearingDatabaseAlsoClearsTheSharedMetadataCache()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string inputPath = temporaryDirectory.CreateFile("input.txt", "before");
        var cache = new DependencyDatabaseCache();
        DependencyDatabase firstDatabase = DependencyDatabase.CreateCSV(
            temporaryDirectory.Path,
            "first",
            cache);
        firstDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null);
        DependencyDatabase secondDatabase = DependencyDatabase.CreateCSV(
            temporaryDirectory.Path,
            "second",
            cache);
        secondDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null);
        WriteWithNewTimestamp(inputPath, "after");

        firstDatabase.ClearDatabase();
        DependencyDatabase verificationDatabase = DependencyDatabase.CreateCSV(
            temporaryDirectory.Path,
            "second",
            cache);

        Assert.True(
            verificationDatabase.RunIfOutdated("compile", _ => { }, [inputPath], args: null));
    }

    private static DependencyDatabase CreateDatabase(
        TemporaryDirectory temporaryDirectory,
        DependencyDatabaseCache? cache = null,
        bool defaultUseSHA = false) =>
        DependencyDatabase.CreateCSV(
            temporaryDirectory.Path,
            DatabaseName,
            cache,
            defaultUseSHA);

    private static string GetDatabasePath(TemporaryDirectory temporaryDirectory) =>
        Path.Combine(temporaryDirectory.Path, DatabaseName + ".db");

    private static string GetRecordPath(TemporaryDirectory temporaryDirectory, string canonicalKey) =>
        Path.Combine(GetDatabasePath(temporaryDirectory), canonicalKey + ".csv");

    private static void ReplacePersistedSha(string recordPath, string digest)
    {
        string[] lines = File.ReadAllLines(recordPath);
        int fileRowIndex = Array.FindIndex(
            lines,
            static line => line.StartsWith("input_file,", StringComparison.Ordinal));
        Assert.True(fileRowIndex >= 0);

        int digestSeparatorIndex = lines[fileRowIndex].LastIndexOf(',');
        Assert.True(digestSeparatorIndex >= 0);
        lines[fileRowIndex] = lines[fileRowIndex][..(digestSeparatorIndex + 1)] + digest;
        File.WriteAllLines(recordPath, lines);
    }

    private static void WriteWithNewTimestamp(string path, string content)
    {
        DateTime originalTimestamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, originalTimestamp.AddMinutes(5));
        Assert.NotEqual(originalTimestamp, File.GetLastWriteTimeUtc(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Incant.UnitTest.Base",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string CreateFile(string name, string content)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class TestBuildException : Exception
    {
    }

    private sealed class MemoryDependencyDatabaseBackend : IDependencyDatabaseBackend
    {
        public DependencyRecord? Load(string key) => _record;

        public void Save(DependencyRecord record)
        {
            _record = record;
        }

        public void Clear()
        {
            _record = null;
        }

        private DependencyRecord? _record;
    }

    private const string DatabaseName = "dependencies";
}
