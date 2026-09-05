using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Incant.Base.Deps;

namespace Incant.UnitTest.Base.Deps;

/// <summary>Exercises recovery through public databases and damaged on-disk records.</summary>
public sealed class DatabaseRecoveryTests
{
    [Theory]
    [InlineData("truncated")]
    [InlineData("checksum")]
    [InlineData("format")]
    [InlineData("unframed")]
    public void OpeningTruncatesInvalidTailWithoutRewritingEarlierRecords(string damage)
    {
        using var directory = new DatabaseTestDirectory();
        byte[] original = CreateHistory(directory);
        List<(int Start, int Content, int End)> frames = FindFrames(original);
        (int Start, int Content, int End) tail = frames[^1];
        byte[] validPrefix = original[..frames[^2].End];
        byte[] damaged = damage switch
        {
            "truncated" => original[..^1],
            "format" => [.. original[..tail.Start], .. Frame("key,suffix\nmode,invalid\n"u8.ToArray(), "2")],
            "unframed" => [.. validPrefix, .. "unfinished header"u8],
            _ => (byte[])original.Clone()
        };
        if (damage == "checksum")
        {
            damaged[tail.Content + 2] ^= 1;
        }

        File.WriteAllBytes(directory.DatabasePath, damaged);
        Database reader = directory.Create(readOnly: true);
        using (Database.OpenScoped(reader))
        {
            Assert.Equal(2, reader.Count);
            Assert.Equal(damaged, File.ReadAllBytes(directory.DatabasePath));
        }

        Database writer = directory.Create();
        using (Database.OpenScoped(writer))
        {
            Assert.Equal(validPrefix, ReadSharedBytes(directory.DatabasePath));
            Assert.Equal(3, writer.TotalRecordCount);
            Assert.False(writer.IsOutdated("subject", null, ["new"]));
            Assert.True(writer.IsOutdated("suffix", null, ["kept"]));
            writer.RunIfOutdated("after", _ => { }, null, null);
        }

        using DatabaseScope reopened = Database.OpenScoped(reader);
        Assert.Equal(3, reader.Count);
        Assert.False(reader.IsOutdated("after", null, null));
        Assert.True(reader.IsOutdated("suffix", null, ["kept"]));
    }

    [Fact]
    public void OpeningImmediatelyCompactsMiddleDamageAndKeepsTheLatestValidRecords()
    {
        using var directory = new DatabaseTestDirectory();
        byte[] damaged = CreateHistory(directory);
        (int Start, int Content, int End) target = FindFrames(damaged)[2];
        damaged[target.Content + 2] ^= 1;
        File.WriteAllBytes(directory.DatabasePath, damaged);

        Database reader = directory.Create(readOnly: true);
        using (Database.OpenScoped(reader))
        {
            Assert.False(reader.IsOutdated("subject", null, ["old"]));
            Assert.Equal(damaged, File.ReadAllBytes(directory.DatabasePath));
        }

        // Required recovery compaction must complete before the writer becomes usable.
        string compactPath = directory.DatabasePath + ".compact";
        Directory.CreateDirectory(compactPath);
        Database writer = directory.Create();
        Exception? failure = Xunit.Record.Exception(writer.Open);
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.False(writer.IsOpened);
        Assert.Equal(damaged, File.ReadAllBytes(directory.DatabasePath));
        Directory.Delete(compactPath);

        using (Database.OpenScoped(writer))
        {
            Assert.Equal(writer.Count, writer.TotalRecordCount);
            Assert.False(writer.IsOutdated("subject", null, ["old"]));
            Assert.False(writer.IsOutdated("prefix", null, ["kept"]));
            Assert.False(writer.IsOutdated("suffix", null, ["kept"]));
            byte[] recovered = ReadSharedBytes(directory.DatabasePath);
            Assert.False(damaged.SequenceEqual(recovered));
            writer.Compact();
            Assert.Equal(recovered, ReadSharedBytes(directory.DatabasePath));
        }

        using DatabaseScope reopened = Database.OpenScoped(reader);
        Assert.Equal(3, reader.Count);
        Assert.Equal(3, reader.TotalRecordCount);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("count")]
    [InlineData("content")]
    [InlineData("format")]
    [InlineData("utf8")]
    [InlineData("terminator")]
    public void DamagedLatestRecordFallsBackAndDoesNotHideLaterRecords(string damage)
    {
        using var directory = new DatabaseTestDirectory();
        byte[] bytes = CreateHistory(directory);
        List<(int Start, int Content, int End)> frames = FindFrames(bytes);
        (int Start, int Content, int End) target = frames[2];
        if (damage == "header")
        {
            bytes[target.Content - 2] = bytes[target.Content - 2] == 'A' ? (byte)'B' : (byte)'A';
        }
        else if (damage == "count")
        {
            bytes[target.Start + "@record,".Length] = (byte)'9';
        }
        else if (damage == "content")
        {
            bytes[target.Content + 2] ^= 1;
        }
        else if (damage == "terminator")
        {
            bytes[target.End - 1] = (byte)'x';
        }
        else
        {
            byte[] content = bytes[target.Content..target.End];
            if (damage == "utf8")
            {
                content[^2] = 0xFF;
            }
            else
            {
                content[0] = 0x7F;
            }
            bytes = [.. bytes[..target.Start], .. Frame(content, "3"), .. bytes[target.End..]];
        }

        File.WriteAllBytes(directory.DatabasePath, bytes);
        Database snapshot = directory.Create(readOnly: true);
        using (Database.OpenScoped(snapshot))
        {
            Assert.Equal(3, snapshot.Count);
            Assert.Equal(3, snapshot.TotalRecordCount);
            Assert.False(snapshot.IsOutdated("subject", null, ["old"]));
            Assert.False(snapshot.IsOutdated("prefix", null, ["kept"]));
            Assert.False(snapshot.IsOutdated("suffix", null, ["kept"]));
        }

        Database writer = directory.Create();
        using (Database.OpenScoped(writer))
        {
            Assert.True(writer.RunIfOutdated("subject", _ => { }, null, ["recovered"]));
            writer.Compact();
            Assert.Equal(3, writer.TotalRecordCount);
        }

        Database reopened = directory.Create(readOnly: true);
        using DatabaseScope reopenedScope = Database.OpenScoped(reopened);
        Assert.False(reopened.IsOutdated("subject", null, ["recovered"]));
        Assert.False(reopened.IsOutdated("suffix", null, ["kept"]));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483647")]
    [InlineData("2147483648")]
    public void InvalidOrExaggeratedRowCountsDoNotConsumeTheRemainingDatabase(string count)
    {
        using var directory = new DatabaseTestDirectory();
        byte[] bytes = CreateHistory(directory);
        (int Start, int Content, int End) target = FindFrames(bytes)[2];
        byte[] replacement = Frame(bytes[target.Content..target.End], count);
        File.WriteAllBytes(directory.DatabasePath, [.. bytes[..target.Start], .. replacement, .. bytes[target.End..]]);
        Database database = directory.Create(readOnly: true);
        using DatabaseScope databaseScope = Database.OpenScoped(database);
        Assert.False(database.IsOutdated("subject", null, ["old"]));
        Assert.False(database.IsOutdated("suffix", null, ["kept"]));
        Assert.Equal(3, database.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryPreservesLargeRecordsAfterAnIncompleteRecord(bool manyArguments)
    {
        using var directory = new DatabaseTestDirectory();
        string[] args = manyArguments
            ? Enumerable.Range(0, 12_000).Select(static index => $"argument-{index}").ToArray()
            : [new string('x', 250_000) + "中文,\"quoted\"\nend"];
        Database database = directory.Create();
        using (Database.OpenScoped(database))
        {
            database.RunIfOutdated("prefix", _ => { }, null, ["kept"]);
            database.RunIfOutdated("large", _ => { }, null, args);
            database.RunIfOutdated("suffix", _ => { }, null, ["kept"]);
        }

        byte[] original = File.ReadAllBytes(directory.DatabasePath);
        int insertion = FindFrames(original)[1].Start;
        // An incomplete record must not consume the following record, regardless of its size or row count.
        byte[] incomplete = Frame("key,torn\nmode,timestamp\n"u8.ToArray(), "2147483647");
        byte[] damaged = [.. original[..insertion], .. incomplete, .. original[insertion..]];
        File.WriteAllBytes(directory.DatabasePath, damaged);

        Database reader = directory.Create(readOnly: true);
        using (Database.OpenScoped(reader))
        {
            Assert.Equal(3, reader.Count);
            Assert.False(reader.IsOutdated("large", null, args));
            Assert.False(reader.IsOutdated("suffix", null, ["kept"]));
            Assert.True(reader.IsOutdated("torn", null, null));
            Assert.Equal(damaged, File.ReadAllBytes(directory.DatabasePath));
        }

        using (Database.OpenScoped(database))
        {
            Assert.Equal(3, database.TotalRecordCount);
            Assert.False(database.IsOutdated("large", null, args));
            database.RunIfOutdated("after", _ => { }, null, ["appended"]);
        }

        using DatabaseScope reopened = Database.OpenScoped(reader);
        Assert.Equal(4, reader.Count);
        Assert.False(reader.IsOutdated("prefix", null, ["kept"]));
        Assert.False(reader.IsOutdated("large", null, args));
        Assert.False(reader.IsOutdated("suffix", null, ["kept"]));
        Assert.False(reader.IsOutdated("after", null, ["appended"]));
        Assert.True(reader.IsOutdated("torn", null, null));
    }

    [Fact]
    public void EveryTruncatedTailPrefixIsIgnoredWithoutLosingThePreviousCommit()
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        using (Database.OpenScoped(database))
        {
            database.RunIfOutdated("stable", _ => { }, null, ["kept"]);
            database.RunIfOutdated("tail", _ => { }, null, ["incomplete"]);
        }

        byte[] complete = File.ReadAllBytes(directory.DatabasePath);
        (int Start, int Content, int End) tail = FindFrames(complete)[1];
        for (int length = tail.Start; length < complete.Length; length++)
        {
            File.WriteAllBytes(directory.DatabasePath, complete[..length]);
            Database snapshot = directory.Create(readOnly: true);
            using DatabaseScope snapshotScope = Database.OpenScoped(snapshot);
            Assert.Equal(1, snapshot.Count);
            Assert.False(snapshot.IsOutdated("stable", null, ["kept"]));
            Assert.True(snapshot.IsOutdated("tail", null, ["incomplete"]));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void AppendingAfterATornTailDoesNotResurrectTheUncommittedRecord(int missingBytes)
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        using (Database.OpenScoped(database))
        {
            database.RunIfOutdated("stable", _ => { }, null, null);
            database.RunIfOutdated("torn", _ => { }, null, ["value"]);
        }

        byte[] bytes = File.ReadAllBytes(directory.DatabasePath);
        File.WriteAllBytes(directory.DatabasePath, bytes[..^missingBytes]);
        Database writer = directory.Create();
        using (Database.OpenScoped(writer))
        {
            Assert.True(writer.IsOutdated("torn", null, ["value"]));
            writer.RunIfOutdated("after", _ => { }, null, null);
        }

        Database reopened = directory.Create(readOnly: true);
        using DatabaseScope reopenedScope = Database.OpenScoped(reopened);
        Assert.Equal(2, reopened.Count);
        Assert.False(reopened.IsOutdated("stable", null, null));
        Assert.True(reopened.IsOutdated("torn", null, ["value"]));
        Assert.False(reopened.IsOutdated("after", null, null));
    }

    [Fact]
    public void TailRecoveryCannotCompleteAFormerRecordByMatchingItsMissingText()
    {
        using var directory = new DatabaseTestDirectory();
        const string MissingSuffix = "!invalid-tail";
        Database database = directory.Create();
        using (Database.OpenScoped(database))
        {
            database.RunIfOutdated("stable", _ => { }, null, null);
            database.RunIfOutdated("torn", _ => { }, null, ["value" + MissingSuffix]);
        }

        byte[] bytes = File.ReadAllBytes(directory.DatabasePath);
        File.WriteAllBytes(directory.DatabasePath, bytes[..^(MissingSuffix.Length + 1)]);
        Database writer = directory.Create();
        using (Database.OpenScoped(writer))
        {
            Assert.True(writer.IsOutdated("torn", null, ["value" + MissingSuffix]));
            writer.RunIfOutdated("after", _ => { }, null, null);
        }

        Database reopened = directory.Create(readOnly: true);
        using DatabaseScope reopenedScope = Database.OpenScoped(reopened);
        Assert.Equal(2, reopened.Count);
        Assert.False(reopened.IsOutdated("stable", null, null));
        Assert.True(reopened.IsOutdated("torn", null, ["value" + MissingSuffix]));
        Assert.False(reopened.IsOutdated("after", null, null));
    }

    [Fact]
    public void InvalidFileHeadersBehaveLikeMissingWithoutReadOnlyMutations()
    {
        using var directory = new DatabaseTestDirectory();
        byte[][] invalidFiles =
        [
            [],
            "not a database\n"u8.ToArray(),
            Header("@incant-deps,99,csv,0,0,0,0"),
            Header("@incant-deps,1,unknown,0,0,0,0"),
            Header("@incant-deps,1,csv,1,0,0,0"),
            Header("@incant-deps,1,csv,0,0,0,0")[..^1]
        ];
        foreach (byte[] invalid in invalidFiles)
        {
            File.WriteAllBytes(directory.DatabasePath, invalid);
            Database reader = directory.Create(readOnly: true);
            using (Database.OpenScoped(reader))
            {
                Assert.Equal(0, reader.Count);
                Assert.True(reader.IsOutdated("key", null, null));
                Assert.Equal(invalid, File.ReadAllBytes(directory.DatabasePath));
            }

            Database writer = directory.Create();
            using (Database.OpenScoped(writer))
            {
                Assert.Equal(0, writer.Count);
                Assert.True(writer.RunIfOutdated("key", _ => { }, null, null));
            }

            Database verified = directory.Create(readOnly: true);
            using DatabaseScope verifiedScope = Database.OpenScoped(verified);
            Assert.False(verified.IsOutdated("key", null, null));
        }
    }

    [Fact]
    public void OpeningCompactsLargeHistoriesWhileReadOnlyOpeningLeavesThemUntouched()
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        using (Database.OpenScoped(database))
        {
            database.RunIfOutdated("key", _ => { }, null, null);
        }

        byte[] original = File.ReadAllBytes(directory.DatabasePath);
        (int Start, int Content, int End) frame = FindFrames(original)[0];
        using (var file = new FileStream(directory.DatabasePath, FileMode.Create, FileAccess.Write))
        {
            file.Write(original[..frame.Start]);
            for (int index = 0; index < 1001; index++)
            {
                file.Write(original[frame.Start..frame.End]);
                file.WriteByte((byte)'\n');
            }
        }

        long size = new FileInfo(directory.DatabasePath).Length;
        Database reader = directory.Create(readOnly: true);
        using (Database.OpenScoped(reader))
        {
            Assert.Equal(1001, reader.TotalRecordCount);
            Assert.Equal(size, new FileInfo(directory.DatabasePath).Length);
        }

        Database writer = directory.Create();
        string compactPath = directory.DatabasePath + ".compact";
        Directory.CreateDirectory(compactPath);
        Exception? failure = Xunit.Record.Exception(writer.Open);
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.False(writer.IsOpened);
        Assert.Equal(size, new FileInfo(directory.DatabasePath).Length);
        Directory.Delete(compactPath);

        // Retrying after a failed load must neither retain nor double-count the loaded history.
        File.WriteAllText(compactPath, "abandoned temporary data");
        using DatabaseScope writerScope = Database.OpenScoped(writer);
        Assert.Equal(1, writer.TotalRecordCount);
        Assert.False(writer.IsOutdated("key", null, null));
        Assert.True(new FileInfo(directory.DatabasePath).Length < size);
        Assert.False(File.Exists(directory.DatabasePath + ".compact"));
    }

    [Fact]
    public void WindowsSharingFailureDoesNotReplaceTheCurrentDatabase()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows sharing flags control replacement in this test.");
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        using DatabaseScope databaseScope = Database.OpenScoped(database);
        database.RunIfOutdated("key", _ => { }, null, ["latest"]);
        byte[] before = ReadSharedBytes(directory.DatabasePath);
        using (var blocker = new FileStream(directory.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.ThrowsAny<IOException>(() => database.Compact());
            Assert.Equal(before, ReadSharedBytes(directory.DatabasePath));
            Assert.False(database.IsOutdated("key", null, ["latest"]));
        }

        database.Compact();
        Assert.False(database.IsOutdated("key", null, ["latest"]));
    }

    [Fact]
    public void ExistingCsvFormatRemainsCompatibleThroughAppendAndCompaction()
    {
        using var directory = new DatabaseTestDirectory();
        byte[] fileHeader = Header("@incant-deps,1,csv,0,0,0,0");
        byte[] originalContent = "key,fixture\nmode,timestamp\ninput_arg,中文\ninput_arg,line\\nnext\ninput_arg,\n"u8.ToArray();
        byte[] original = [.. fileHeader, (byte)'\n', .. Frame(originalContent, "5")];
        File.WriteAllBytes(directory.DatabasePath, original);

        Database database = directory.Create();
        using (Database.OpenScoped(database))
        {
            Assert.False(database.RunIfOutdated("Fixture", _ => Assert.Fail(), null, ["中文", "line\nnext", ""]));
            Assert.Equal(original, ReadSharedBytes(directory.DatabasePath));
            Assert.True(database.RunIfOutdated("fixture", _ => { }, null, ["changed"]));
            byte[] changed = Frame("key,fixture\nmode,timestamp\ninput_arg,changed\n"u8.ToArray(), "3");
            Assert.Equal([.. original, (byte)'\n', .. changed], ReadSharedBytes(directory.DatabasePath));

            database.Compact();
            Assert.Equal([.. fileHeader, (byte)'\n', .. changed], ReadSharedBytes(directory.DatabasePath));
        }

        Database reopened = directory.Create(readOnly: true);
        using DatabaseScope reopenedScope = Database.OpenScoped(reopened);
        Assert.Equal(1, reopened.Count);
        Assert.False(reopened.IsOutdated("fixture", null, ["changed"]));
    }

    [Theory]
    [InlineData("key,subject\nkey,subject\nmode,timestamp\n")]
    [InlineData("key,subject\nmode,timestamp\nmode,timestamp\n")]
    [InlineData("key,subject\nmode,unknown\n")]
    [InlineData("key,subject\n")]
    [InlineData("key,Subject\nmode,timestamp\n")]
    [InlineData("key,\nmode,timestamp\n")]
    [InlineData("key,CON\nmode,timestamp\n")]
    [InlineData("key,a/b\nmode,timestamp\n")]
    [InlineData("key,subject\nmode,timestamp\nunknown,value\n")]
    [InlineData("key,subject\nmode,timestamp\ninput_arg,extra,field\n")]
    [InlineData("key,subject\nmode,timestamp\ninput_arg,\"unterminated\n")]
    [InlineData("key,subject\nmode,timestamp\ninput_arg,un\"quoted\n")]
    [InlineData("key,subject\nmode,timestamp\ninput_arg,bad\\escape\n")]
    [InlineData("key,subject\nmode,timestamp\ninput_file,file,invalid,\n")]
    [InlineData("key,subject\nmode,timestamp\ninput_file,file,3155378976000000000,\n")]
    [InlineData("key,subject\nmode,timestamp\ninput_file,,0,\n")]
    [InlineData("key,subject\nmode,timestamp\ninput_file,b,0,\ninput_file,a,0,\n")]
    [InlineData("key,subject\nmode,timestamp\ninput_file,a,0,\ninput_file,a,0,\n")]
    [InlineData("key,subject\nmode,timestamp\nexternal_file,,0,\n")]
    [InlineData("key,subject\nmode,sha256\ninput_file,file,0,invalid\n")]
    public void ChecksummedInvalidRowsAreIgnoredWithoutHidingOtherRecords(string content)
    {
        using var directory = new DatabaseTestDirectory();
        byte[] bytes = CreateHistory(directory);
        (int Start, int Content, int End) target = FindFrames(bytes)[2];
        string rowCount = content.Count(static character => character == '\n').ToString(CultureInfo.InvariantCulture);
        byte[] invalid = Frame(Encoding.UTF8.GetBytes(content), rowCount);
        File.WriteAllBytes(directory.DatabasePath, [.. bytes[..target.Start], .. invalid, .. bytes[target.End..]]);

        Database database = directory.Create(readOnly: true);
        using DatabaseScope databaseScope = Database.OpenScoped(database);
        Assert.Equal(3, database.Count);
        Assert.False(database.IsOutdated("subject", null, ["old"]));
        Assert.False(database.IsOutdated("prefix", null, ["kept"]));
        Assert.False(database.IsOutdated("suffix", null, ["kept"]));
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var contents = new MemoryStream();
        stream.CopyTo(contents);
        return contents.ToArray();
    }

    private static byte[] CreateHistory(DatabaseTestDirectory directory)
    {
        Database database = directory.Create();
        using (Database.OpenScoped(database))
        {
            database.RunIfOutdated("prefix", _ => { }, null, ["kept"]);
            database.RunIfOutdated("subject", _ => { }, null, ["old"]);
            database.RunIfOutdated("subject", _ => { }, null, ["new"]);
            database.RunIfOutdated("suffix", _ => { }, null, ["kept"]);
        }

        return File.ReadAllBytes(directory.DatabasePath);
    }

    private static List<(int Start, int Content, int End)> FindFrames(byte[] bytes)
    {
        var frames = new List<(int Start, int Content, int End)>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            int relative = bytes.AsSpan(offset).IndexOf("@record,"u8);
            if (relative < 0)
            {
                break;
            }

            int start = offset + relative;
            int content = start + bytes.AsSpan(start).IndexOf((byte)'\n') + 1;
            string[] fields = Encoding.ASCII.GetString(bytes, start, content - start - 1).Split(',');
            int rows = int.Parse(fields[1], CultureInfo.InvariantCulture);
            int end = content;
            for (int index = 0; index < rows; index++)
            {
                end += bytes.AsSpan(end).IndexOf((byte)'\n') + 1;
            }

            frames.Add((start, content, end));
            offset = end;
        }

        return frames;
    }

    private static byte[] Frame(byte[] content, string rowCount) =>
        [.. Header("@record," + rowCount + "," + Convert.ToHexString(MD5.HashData(content))), .. content];

    private static byte[] Header(string prefix) =>
        Encoding.ASCII.GetBytes(prefix + "," + Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(prefix))) + "\n");
}
