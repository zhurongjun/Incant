using Incant.Base;

namespace Incant.UnitTest.Base;

public sealed class FileListTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Incant.UnitTest.Base", "FileList", Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = [];

    [Fact]
    public void ConstructionFixesTheRootWithoutCreatingIt()
    {
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, At("not-created"));
        var files = new FileList(relative);

        Assert.Equal(At("not-created"), files.RootDirectory);
        Assert.Empty(files.Files);
        Assert.Same(files.Files, files.Resolve());
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void ExactPathsAreOrderedNormalizedDeclarationsIncludingFilesOutsideTheRoot()
    {
        var files = new FileList(At("source"));
        files.Add("z.cpp", "./nested/../a.cpp", "z.cpp", "../generated.cpp", At("external.cpp"));

        Assert.Equal(new[] { At("source/z.cpp"), At("source/a.cpp"), At("generated.cpp"), At("external.cpp") }, Paths(files));
        Assert.All(files.Files, entry => Assert.Empty(entry.Metadata));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void CommandsOnlyAffectEntriesPresentAtTheirPosition()
    {
        var first = new Label("first");
        var second = new Label("second");
        var files = new FileList(_directory);
        files.SetMetadata(first).Remove("*.cpp")
            .Add("a.cpp", "b.cpp").SetMetadata("*.cpp", first)
            .Remove("a.cpp").Add("c.cpp", "a.cpp")
            .SetMetadata("a.cpp", second).Add("b.cpp");

        Assert.Equal(new[] { At("b.cpp"), At("c.cpp"), At("a.cpp") }, Paths(files));
        Assert.Equal(new FileMetadata[] { first }, files.Files[0].Metadata);
        Assert.Empty(files.Files[1].Metadata);
        Assert.Equal(new FileMetadata[] { second }, files.Files[2].Metadata);
    }

    [Fact]
    public void MetadataAppendsDuplicateObjectsAndTypesInCommandAndArgumentOrder()
    {
        var first = new Label("one");
        var second = new Label("two");
        var count = new Count(3);
        var files = new FileList(_directory);
        files.Add("a.cpp").SetMetadata(first, first, second).SetMetadata("a.cpp", count, first);

        Assert.Equal(new FileMetadata[] { first, first, second, count, first }, Assert.Single(files.Files).Metadata);
        Assert.Same(first, files.Files[0].Metadata[0]);
    }

    [Fact]
    public void MemoryGlobsIncludeMissingFilesAndTrackDirectoryRemovalAndRecreation()
    {
        var tag = new Label("generated");
        var files = new FileList(_directory);
        files.Add("gen/first.cpp", "gen/sub/second.cpp", "other.cpp")
            .SetMetadata("gen/**/*.cpp", tag)
            .Remove("gen/**/*.cpp")
            .Add("gen/new.cpp")
            .SetMetadata("gen/*.cpp", tag);

        Assert.Equal(new[] { At("other.cpp"), At("gen/new.cpp") }, Paths(files));
        Assert.Empty(files.Files[0].Metadata);
        Assert.Equal(new FileMetadata[] { tag }, files.Files[1].Metadata);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void AbsoluteAndParentSelectorsMatchRootExternalDeclarations()
    {
        var tag = new Label("external");
        var files = new FileList(At("source"));
        files.Add("../shared/a.cpp", "../shared/deep/b.cpp", "local.cpp")
            .SetMetadata("../shared/**/*.cpp", tag)
            .Remove(At("shared/deep/*.cpp"))
            .SetMetadata(new Count(2));

        Assert.Equal(new[] { At("shared/a.cpp"), At("source/local.cpp") }, Paths(files));
        Assert.Same(tag, files.Files[0].Metadata[0]);
        Assert.IsType<Count>(Assert.Single(files.Files[1].Metadata));
    }

    [Fact]
    public void PathComparisonIsConsistentForExactAndGlobOperations()
    {
        var tag = new Label("matched");
        var files = new FileList(_directory);
        files.Add("Src/FILE.CPP", "Src/file.cpp").SetMetadata("src/*.cpp", tag);

        if (OperatingSystem.IsWindows())
        {
            FileEntry entry = Assert.Single(files.Files);
            Assert.Equal(At("Src/FILE.CPP"), entry.Path);
            Assert.Same(tag, Assert.Single(entry.Metadata));
            files.Remove("SRC/file.cpp");
            Assert.Empty(files.Files);
        }
        else
        {
            Assert.Equal(2, files.Files.Count);
            Assert.All(files.Files, entry => Assert.Empty(entry.Metadata));
            files.Remove("Src/*.CPP");
            Assert.Equal(At("Src/file.cpp"), Assert.Single(files.Files).Path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InputsAreCopiedBeforeCommandsAreQueued(bool selectByPattern)
    {
        var original = new Label("original");
        string[] additions = ["a.cpp", "b.cpp"];
        string[] removals = ["b.cpp"];
        FileMetadata[] metadata = [original];
        var files = new FileList(_directory);
        files.Add(additions).Remove(removals);
        if (selectByPattern)
        {
            files.SetMetadata("*.cpp", metadata);
        }
        else
        {
            files.SetMetadata(metadata);
        }
        additions[0] = "changed.cpp";
        removals[0] = "a.cpp";
        metadata[0] = new Label("changed");

        FileEntry entry = Assert.Single(files.Files);
        Assert.Equal(At("a.cpp"), entry.Path);
        Assert.Same(original, Assert.Single(entry.Metadata));
    }

    [Fact]
    public void SnapshotsRemainReadOnlyAndUnchangedAfterLaterCommands()
    {
        var first = new Label("first");
        var files = new FileList(_directory).Add("a.cpp").SetMetadata(first);
        IReadOnlyList<FileEntry> before = files.Files;
        if (before is IList<FileEntry> mutableFiles)
        {
            Assert.Throws<NotSupportedException>(() => mutableFiles.Clear());
        }
        if (before[0].Metadata is IList<FileMetadata> mutableMetadata)
        {
            Assert.Throws<NotSupportedException>(() => mutableMetadata.Clear());
        }

        files.SetMetadata(new Label("second")).Add("b.cpp");
        Assert.Equal(2, files.Files.Count);
        Assert.Equal(2, files.Files[0].Metadata.Count);
        Assert.Same(first, Assert.Single(Assert.Single(before).Metadata));
    }

    [Fact]
    public void GlobAddSortsEachPatternAndRetainsTheOrderOfPatterns()
    {
        Write("src/z.cpp");
        Write("src/a.cpp");
        Write("src/sub/b.cpp");
        Write("other/q.cpp");
        Write("src/ignored.h");
        var files = new FileList(_directory);
        files.Add("other/*.cpp", "src/*.cpp", "src/**/*.cpp", "src/a.cpp");

        Assert.Equal(new[] { At("other/q.cpp"), At("src/a.cpp"), At("src/z.cpp"), At("src/sub/b.cpp") }, Paths(files));
    }

    [Fact]
    public void GlobOperationsReplayAroundMetadataAndReaddition()
    {
        Write("a.cpp");
        Write("nested/b.cpp");
        var first = new Label("first");
        var last = new Label("last");
        var files = new FileList(_directory);
        files.Add("**/*.cpp").SetMetadata(first)
            .Remove("*.cpp").Add("*.cpp").SetMetadata("*.cpp", last);

        Assert.Equal(new[] { At("nested/b.cpp"), At("a.cpp") }, Paths(files));
        Assert.Equal(new FileMetadata[] { first }, files.Files[0].Metadata);
        Assert.Equal(new FileMetadata[] { last }, files.Files[1].Metadata);
    }

    [Fact]
    public void AbsoluteParentAndDirectoryGlobsHaveTheSameSelectionSemantics()
    {
        Write("shared/a.cpp");
        Write("shared/sub/b.cpp");
        Write("shared/sub/readme.txt");
        var relative = new FileList(At("source")).Add("../shared/**/*.cpp");
        var absolute = new FileList(At("source")).Add(At("shared/**/*.cpp"));
        var directory = new FileList(_directory).Add("shared/").Remove("**/*.txt");

        string[] expected = [At("shared/a.cpp"), At("shared/sub/b.cpp")];
        Assert.Equal(expected, Paths(relative));
        Assert.Equal(expected, Paths(absolute));
        Assert.Equal(expected, Paths(directory));
    }

    [Fact]
    public void GlobComparisonUsesThePlatformRule()
    {
        Write("source/a.CPP");
        var files = new FileList(_directory).Add("source/*.cpp");

        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, files.Files.Count);
    }

    [Fact]
    public void CommandsAreLazyAndOrdinaryResolveReusesTheSnapshot()
    {
        var files = new FileList(_directory).Add("*.cpp");
        Assert.False(Directory.Exists(_directory));
        Write("first.cpp");
        IReadOnlyList<FileEntry> first = files.Files;
        Write("second.cpp");

        Assert.Equal(At("first.cpp"), Assert.Single(first).Path);
        Assert.Same(first, files.Resolve());
        Assert.Same(first, files.Files);
        Assert.Equal(2, files.Resolve(force: true).Count);
        Assert.Single(first);
    }

    [Fact]
    public void EveryKindOfCommandInvalidatesAndReplaysDiskGlobs()
    {
        Write("a.cpp");
        var tag = new Label("tag");
        var files = new FileList(_directory).Add("*.cpp");
        Assert.Single(files.Files);
        Write("b.cpp");
        files.SetMetadata(tag);
        Assert.Equal(2, files.Files.Count);
        Write("c.cpp");
        files.Remove("b.cpp");
        Assert.Equal(new[] { At("a.cpp"), At("c.cpp") }, Paths(files));
        Write("d.cpp");
        files.Add("explicit.cpp");
        Assert.Equal(new[] { At("a.cpp"), At("c.cpp"), At("d.cpp"), At("explicit.cpp") }, Paths(files));
    }

    [Fact]
    public void ClearingCommandsDropsTheirEffectsWithoutMutatingExistingSnapshots()
    {
        Write("a.cpp");
        var files = new FileList(_directory).Add("*.cpp").SetMetadata(new Label("tag"));
        IReadOnlyList<FileEntry> before = files.Files;
        files.ClearCommands();

        Assert.Empty(files.Files);
        Assert.Single(Assert.Single(before).Metadata);
        files.Add("b.cpp");
        Assert.Equal(At("b.cpp"), Assert.Single(files.Files).Path);
        Assert.Empty(files.Files[0].Metadata);
    }

    [Fact]
    public void EmptyOperationsDoNotInvalidateAnExistingSnapshot()
    {
        var files = new FileList(_directory).Add("a.cpp");
        IReadOnlyList<FileEntry> before = files.Files;
        files.Add().Remove().SetMetadata().SetMetadata("*.cpp");

        Assert.Same(before, files.Files);
    }

    [Fact]
    public void InvalidBatchesAreRejectedAtomicallyWithRelevantParameterNames()
    {
        var files = new FileList(_directory).Add("a.cpp", "b.cpp");
        IReadOnlyList<FileEntry> before = files.Files;
        Assert.Equal("paths", Assert.Throws<ArgumentNullException>(() => files.Add((string[])null!)).ParamName);
        Assert.Equal("paths", Assert.Throws<ArgumentNullException>(() => files.Add("new.cpp", null!)).ParamName);
        Assert.Equal("paths", Assert.Throws<ArgumentException>(() => files.Remove("a.cpp", " ")).ParamName);
        Assert.Equal("paths", Assert.Throws<ArgumentException>(() => files.Add("new.cpp", "bad\0name")).ParamName);
        Assert.Equal("pattern", Assert.Throws<ArgumentNullException>(() => files.SetMetadata((string)null!, new Label("x"))).ParamName);
        Assert.Equal("pattern", Assert.Throws<ArgumentException>(() => files.SetMetadata(" ", new Label("x"))).ParamName);
        Assert.Equal("metadata", Assert.Throws<ArgumentNullException>(() => files.SetMetadata((FileMetadata[])null!)).ParamName);
        Assert.Equal("metadata", Assert.Throws<ArgumentNullException>(() => files.SetMetadata(new Label("x"), null!)).ParamName);
        Assert.Equal("metadata", Assert.Throws<ArgumentNullException>(() => files.SetMetadata("a.cpp", new Label("x"), null!)).ParamName);
        Assert.Same(before, files.Files);
        Assert.All(files.Files, entry => Assert.Empty(entry.Metadata));
    }

    [Fact]
    public void InvalidRootsAndPatternsFailBeforeResolution()
    {
        Assert.Equal("rootDirectory", Assert.Throws<ArgumentNullException>(() => new FileList(null!)).ParamName);
        Assert.Equal("rootDirectory", Assert.Throws<ArgumentException>(() => new FileList(" ")).ParamName);
        Assert.Equal("rootDirectory", Assert.Throws<ArgumentException>(() => new FileList("bad\0path")).ParamName);
        var files = new FileList(_directory);
        Assert.Throws<ArgumentException>(() => files.Add("*/../bad.cpp"));
        Assert.Empty(files.Files);
    }

    [Fact]
    public void FailedResolutionDoesNotPublishPartialResultsAndCanBeRetried()
    {
        Write("broken");
        var files = new FileList(_directory).Add("old.cpp");
        IReadOnlyList<FileEntry> before = files.Files;
        files.Add("new.cpp", "broken/*.cpp");
        Assert.Throws<IOException>(() => files.Files);
        Assert.Throws<IOException>(() => files.Resolve());
        Assert.Equal(At("old.cpp"), Assert.Single(before).Path);

        File.Delete(At("broken"));
        Write("broken/recovered.cpp");
        Assert.Equal(new[] { At("old.cpp"), At("new.cpp"), At("broken/recovered.cpp") }, Paths(files));
    }

    [Fact]
    public void FailedForcedRefreshDoesNotLeaveAValidCacheFlag()
    {
        Write("source/a.cpp");
        var files = new FileList(_directory).Add("source/*.cpp");
        IReadOnlyList<FileEntry> before = files.Files;
        File.Delete(At("source/a.cpp"));
        Directory.Delete(At("source"));
        Write("source");
        Assert.Throws<IOException>(() => files.Resolve(force: true));
        Assert.Throws<IOException>(() => files.Files);
        Assert.Single(before);

        File.Delete(At("source"));
        Write("source/b.cpp");
        Assert.Equal(At("source/b.cpp"), Assert.Single(files.Files).Path);
    }

    [Fact]
    public void DirectoryLinksRetainIndependentPathsAndSkipAncestorCycles()
    {
        Write("tree/a.cpp");
        Write("tree/sub/b.cpp");
        CreateDirectoryLink("alias", "tree");
        CreateDirectoryLink("tree/sub/back", "..");
        var files = new FileList(_directory).Add("**/*.cpp");

        Assert.Equal(new[] { At("alias/a.cpp"), At("alias/sub/b.cpp"), At("tree/a.cpp"), At("tree/sub/b.cpp") }, Paths(files));
        files.SetMetadata("alias/**/*.cpp", new Label("alias"));
        Assert.All(files.Files.Take(2), entry => Assert.Single(entry.Metadata));
        Assert.All(files.Files.Skip(2), entry => Assert.Empty(entry.Metadata));
    }

    [Fact]
    public void ALinkedRootAndConsecutiveLinksUsePhysicalAncestryOnlyForCycleDetection()
    {
        Write("physical/a.cpp");
        CreateDirectoryLink("first", "physical");
        CreateDirectoryLink("second", "first");
        CreateDirectoryLink("physical/back", "../second");
        var files = new FileList(At("second")).Add("**/*.cpp");

        Assert.Equal(At("second/a.cpp"), Assert.Single(files.Files).Path);
    }

    [Fact]
    public async Task WindowsJunctionsPreserveAliasesAndTerminateCycles()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows filesystem feature.");
        Write("tree/a.cpp");
        await CreateJunctionAsync("alias", "tree");
        await CreateJunctionAsync("tree/back", "tree");
        var files = new FileList(_directory).Add("**/*.cpp");

        Assert.Equal(new[] { At("alias/a.cpp"), At("tree/a.cpp") }, Paths(files));
    }

    [Fact]
    public void LargerDeclarationsSupportRepeatedSelectorsWithoutLosingOrderOrAttachments()
    {
        const int Count = 12000;
        string[] paths = Enumerable.Range(0, Count).Select(index => $"group{index % 12}/file{index}.cpp").ToArray();
        var files = new FileList(_directory).Add(paths);
        var tag = new Label("all");
        for (int iteration = 0; iteration < 12; iteration++)
        {
            files.SetMetadata("**/*.cpp", tag);
        }
        files.Remove("group0/**").Add(paths[0]).SetMetadata(paths[0], tag);

        Assert.Equal(11001, files.Files.Count);
        Assert.Equal(At(paths[1]), files.Files[0].Path);
        Assert.Equal(At(paths[0]), files.Files[^1].Path);
        Assert.Equal(12, files.Files[0].Metadata.Count);
        Assert.Single(files.Files[^1].Metadata);
    }

    [Fact]
    public void RepeatedSelectorsObserveMembershipChangesAndForcedReplayDoesNotDuplicateMetadata()
    {
        var first = new Label("first");
        var second = new Label("second");
        var files = new FileList(_directory);
        files.SetMetadata("*.cpp", first)
            .Add("a.cpp").SetMetadata("*.cpp", first)
            .Add("b.cpp").SetMetadata("*.cpp", second)
            .Remove("a.cpp").SetMetadata("*.cpp", second);

        Assert.Equal(At("b.cpp"), Assert.Single(files.Files).Path);
        Assert.Equal(new FileMetadata[] { second, second }, files.Files[0].Metadata);
        files.Resolve(force: true);
        Assert.Equal(new FileMetadata[] { second, second }, files.Files[0].Metadata);
        files.Remove("*.cpp").Add("c.cpp").SetMetadata("*.cpp", first);
        Assert.Equal(At("c.cpp"), Assert.Single(files.Files).Path);
        Assert.Equal(new FileMetadata[] { first }, files.Files[0].Metadata);
    }

    [Fact]
    public void MissingGlobDirectoriesAndEmptyMatchesCanBeRefreshed()
    {
        var files = new FileList(_directory).Add("missing/**/*.cpp");
        Assert.Empty(files.Files);
        Write("missing/readme.txt");
        Assert.Empty(files.Resolve(force: true));
        Write("missing/深 层/文件.cpp");
        Assert.Empty(files.Files);
        Assert.Equal(At("missing/深 层/文件.cpp"), Assert.Single(files.Resolve(force: true)).Path);
    }

    [Fact]
    public void TargetedPatternsDoNotTraverseAnUnrelatedBrokenLink()
    {
        Write("source/a.cpp");
        CreateDirectoryLink("unrelated", "does-not-exist");
        var files = new FileList(_directory).Add("source/*.cpp");

        Assert.Equal(At("source/a.cpp"), Assert.Single(files.Files).Path);
    }

    [Fact]
    public void DirectoryAccessFailuresAreNotConvertedIntoEmptyGlobs()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("This fixture controls access using Unix mode bits.");
            return;
        }
        Write("blocked/a.cpp");
        string blocked = At("blocked");
        UnixFileMode original = File.GetUnixFileMode(blocked);
        File.SetUnixFileMode(blocked, UnixFileMode.None);
        try
        {
            bool denied = false;
            try
            {
                Directory.GetFileSystemEntries(blocked);
            }
            catch (UnauthorizedAccessException)
            {
                denied = true;
            }
            Assert.SkipUnless(denied, "The test account can bypass Unix directory access restrictions.");
            var files = new FileList(_directory).Add("blocked/*.cpp");
            Assert.Throws<UnauthorizedAccessException>(() => files.Files);
        }
        finally
        {
            File.SetUnixFileMode(blocked, original);
        }
    }

    [Fact]
    public void BrokenDirectoryLinksAndLinkOnlyCyclesHaveNoMatchingFiles()
    {
        Write("source/a.cpp");
        CreateDirectoryLink("missing-link", "absent");
        CreateDirectoryLink("cycle-a", "cycle-b");
        CreateDirectoryLink("cycle-b", "cycle-a");
        var files = new FileList(_directory).Add("source/*.cpp", "missing-link/**/*.cpp", "cycle-a/**/*.cpp");

        Assert.Equal(At("source/a.cpp"), Assert.Single(files.Files).Path);
    }

    [Fact]
    public void UnixLinkTargetsResolveIntermediateLinksBeforeParentComponents()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows normalizes parent components before resolving links.");
        Write("physical/deep/unused.txt");
        Write("physical/source/correct.cpp");
        Write("source/wrong.cpp");
        CreateDirectoryLink("bridge", "physical/deep");
        CreateDirectoryLink("entry", "bridge/../source");
        var files = new FileList(_directory).Add("entry/*.cpp");

        Assert.Equal(At("entry/correct.cpp"), Assert.Single(files.Files).Path);
    }

    [Fact]
    public void WindowsDriveRelativePatternsUseTheExplicitRoot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive-relative paths are a Windows path form.");
        string drive = Path.GetPathRoot(_directory)![..2];
        var tag = new Label("drive");
        var files = new FileList(_directory).Add("a.cpp").SetMetadata(drive + "*.cpp", tag);

        Assert.Same(tag, Assert.Single(Assert.Single(files.Files).Metadata));
    }

    [Fact]
    public void DefaultEntriesExposeAnEmptyPathAndMetadataList()
    {
        FileEntry entry = default;

        Assert.Empty(entry.Path);
        Assert.Empty(entry.Metadata);
        Assert.Same(entry.Metadata, entry.Metadata);
    }

    [Fact]
    public void EntryCopiesRetainTheirSnapshotWhenTheListChanges()
    {
        var metadata = new Label("original");
        var files = new FileList(_directory).Add("a.cpp").SetMetadata(metadata);
        FileEntry original = Assert.Single(files.Files);
        FileEntry copy = original;
        original = default;
        files.Remove("a.cpp").Add("b.cpp");

        Assert.Empty(original.Path);
        Assert.Equal(At("a.cpp"), copy.Path);
        Assert.Same(metadata, Assert.Single(copy.Metadata));
        Assert.Equal(At("b.cpp"), Assert.Single(files.Files).Path);
        Assert.Empty(files.Files[0].Metadata);
    }

    public void Dispose()
    {
        foreach (string link in Enumerable.Reverse(_links))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.Delete(link);
            }
            else
            {
                // Unlink the entry even when its target is missing or forms a cycle.
                File.Delete(link);
            }
        }
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string At(string relative) => Path.Combine(_directory, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relative)
    {
        string path = At(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
    }

    private static string[] Paths(FileList files) => files.Files.Select(entry => entry.Path).ToArray();

    private void CreateDirectoryLink(string relative, string target)
    {
        string path = At(relative);
        try
        {
            Directory.CreateSymbolicLink(path, target);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            Assert.Skip("The Windows test account cannot create symbolic links.");
        }
        catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314)
        {
            Assert.Skip("The Windows test account lacks symbolic link privileges.");
        }
        _links.Add(path);
    }

    private async Task CreateJunctionAsync(string relative, string target)
    {
        string path = At(relative);
        ProcessResult result = await Misc.RunProcessAsync(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            ["/d", "/c", "mklink", "/J", path, At(target)],
            new ProcessOptions { Timeout = TimeSpan.FromSeconds(30) },
            TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        _links.Add(path);
    }

    private sealed class Label(string value) : FileMetadata
    {
        internal string Value { get; } = value;
    }

    private sealed class Count(int value) : FileMetadata
    {
        internal int Value { get; } = value;
    }
}
