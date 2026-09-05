using Incant.Core.Cpp;
using Incant.Core.Cpp.FindTools;

namespace Incant.UnitTest.Core.Cpp.FindTools;

#pragma warning disable xUnit1051 // Provider doubles must observe the token supplied by the public API.

public sealed class FinderTests
{
    [Fact]
    public async Task DiscoveryDoesNotResolveToolsAndMissingToolsDoNotRemoveTheEnvironment()
    {
        int lookups = 0;
        var toolSet = new TestToolSet("partial", find: (_, _, _) =>
        {
            ++lookups;
            return Task.FromResult<Tool?>(null);
        });
        Finder finder = CreateFinder(toolSet);

        ToolSet selected = Assert.Single((await finder.FindToolSetsAsync(Query())).ToolSets);
        Assert.Equal(0, lookups);
        Assert.Null(await selected.FindToolAsync(ToolNames.Link));
        Assert.Equal(1, lookups);
        Assert.Null(await selected.FindToolAsync(ToolNames.Link));
        Assert.Equal(2, lookups);
        Assert.Single((await finder.FindToolSetsAsync(Query())).ToolSets);
    }

    [Fact]
    public async Task ConcreteNamesAndTargetRequirementsReachTheSelectedToolSet()
    {
        ToolQuery? observed = null;
        string? observedName = null;
        string path = Root("custom-tool");
        var toolSet = new TestToolSet("custom", find: (name, query, _) =>
        {
            observedName = name;
            observed = query;
            return Task.FromResult<Tool?>(new Tool(name, path, TargetArchitecture.X64, query.TargetArchitecture!.Value));
        });
        ToolSet selected = (await CreateFinder(toolSet).FindToolSetAsync(Query()))!;
        var query = new ToolQuery { TargetArchitecture = TargetArchitecture.ARM64, TargetPlatform = TargetPlatform.Windows };

        Tool? tool = await selected.FindToolAsync("vendor-link", query);

        Assert.Equal("vendor-link", observedName);
        Assert.Equal(query, observed);
        Assert.Equal(path, tool?.Path);
        Assert.Equal(TargetArchitecture.ARM64, tool?.TargetArchitecture);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../cl")]
    [InlineData("directory\\clang")]
    [InlineData(".")]
    [InlineData("..")]
    public void ToolNamesCannotEscapeTheirEnvironment(string name) =>
        Assert.Throws<ArgumentException>(() => new TestToolSet("names").FindTool(name));

    [Fact]
    public async Task NoMatchReturnsNullAndEmptyEnumeration()
    {
        Finder finder = CreateFinder();
        Assert.Empty((await finder.FindToolSetsAsync(Query())).ToolSets);
        Assert.Null(await finder.FindToolSetAsync(Query()));
        Assert.Null(finder.FindToolSet(Query()));
    }

    [Fact]
    public async Task SelectionUsesSourceThenStableVersionAndPreservesConcreteVersions()
    {
        Finder finder = CreateFinder(
            new TestToolSet("same-bin", version: new Version(12, 0), source: Source.Explicit),
            new TestToolSet("same-bin", version: new Version(14, 0), source: Source.Path),
            new TestToolSet("same-bin", version: new Version(18, 0), source: Source.Explicit, channel: Channel.Preview));

        Assert.Equal(new Version(12, 0), (await finder.FindToolSetAsync(Query()))?.Version);
        Assert.Equal(2, (await finder.FindToolSetsAsync(Query())).ToolSets.Count);
        Assert.Equal(new Version(14, 0), (await finder.FindToolSetAsync(Query() with
        {
            Version = new VersionConstraint(minimumInclusive: new Version(13, 0), maximumExclusive: new Version(15, 0)),
        }))?.Version);
        ToolSet? preview = await finder.FindToolSetAsync(Query() with
        {
            IncludePreview = true,
            Version = new VersionConstraint(exact: new Version(18, 0)),
        });
        Assert.Equal(Channel.Preview, preview?.Channel);
    }

    [Theory]
    [InlineData(Channel.Preview)]
    [InlineData(Channel.Experimental)]
    public async Task NonStableChannelsRequireOptIn(Channel channel)
    {
        Finder finder = CreateFinder(new TestToolSet("preview", channel: channel));
        Assert.Null(await finder.FindToolSetAsync(Query()));
        Assert.NotNull(await finder.FindToolSetAsync(Query() with { IncludePreview = true }));
    }

    [Fact]
    public async Task ProductToolSetAndCompilerVersionsAreIndependent()
    {
        var toolSet = new TestToolSet("versions", version: new Version(14, 44),
            productVersion: new Version(17, 14), compilerVersion: new Version(19, 44));
        Finder finder = CreateFinder(toolSet);

        Assert.NotNull(await finder.FindToolSetAsync(Query() with
        {
            ProductVersion = new VersionConstraint(exact: new Version(17, 14)),
            CompilerVersion = new VersionConstraint(exact: new Version(19, 44)),
            Version = new VersionConstraint(exact: new Version(14, 44)),
        }));
        Assert.Null(await finder.FindToolSetAsync(Query() with { CompilerVersion = new VersionConstraint(exact: new Version(14, 44)) }));
    }

    [Fact]
    public async Task DuplicatePathsMergeSourcesButNotVersionsOrTargets()
    {
        Finder finder = CreateFinder(
            new TestToolSet("duplicate", source: Source.Path),
            new TestToolSet("duplicate" + Path.DirectorySeparatorChar, source: Source.Environment),
            new TestToolSet("duplicate", version: new Version(2, 0)),
            new TestToolSet("duplicate", triple: "aarch64-linux-gnu"));

        ToolSet[] items = (await finder.FindToolSetsAsync(Query())).ToolSets.ToArray();
        Assert.Equal(3, items.Length);
        Assert.Equal([Source.Environment, Source.Path], items.First(item => item.Sources.Contains(Source.Environment)).Sources);
    }

    [Fact]
    public async Task ExplicitRootDoesNotFallBackToAnotherInstallation()
    {
        Finder finder = CreateFinder(new TestToolSet("valid"));
        DiscoveryException error = await Assert.ThrowsAsync<DiscoveryException>(() =>
            finder.FindToolSetsAsync(Query() with { RootPath = Root("missing") }));
        Assert.Contains("missing", error.Message);
        Assert.Null(await finder.FindToolSetAsync(Query() with
        {
            RootPath = Root("valid"),
            Version = new VersionConstraint(exact: new Version(99, 0)),
        }));
    }

    [Fact]
    public async Task KindFilterSkipsProvidersAndFiltersTheirMixedResults()
    {
        int unrelatedCalls = 0;
        var unrelated = new TestProvider([Kind.Llvm], (_, _, _) =>
        {
            ++unrelatedCalls;
            return Task.FromResult(new DiscoveryResult());
        });
        var mixed = new TestProvider([Kind.Gnu, Kind.Llvm], (_, _, _) =>
            Task.FromResult(new DiscoveryResult([new TestToolSet("gnu"), new TestToolSet("llvm", kind: Kind.Llvm)])));
        var finder = new Finder([unrelated, mixed]);

        Assert.Equal(Kind.Gnu, Assert.Single((await finder.FindToolSetsAsync(Query() with { Kind = Kind.Gnu })).ToolSets).Kind);
        Assert.Equal(0, unrelatedCalls);
    }

    [Fact]
    public async Task ProvidersRunConcurrentlyAndEveryRequestIsFresh()
    {
        TaskCompletionSource firstStarted = Signal();
        TaskCompletionSource secondStarted = Signal();
        TaskCompletionSource release = Signal();
        int calls = 0;
        TestProvider First(string name, TaskCompletionSource started) => new([Kind.Gnu], async (_, _, token) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return new DiscoveryResult([new TestToolSet(name)]);
        });
        var finder = new Finder([First("one", firstStarted), First("two", secondStarted)]);

        Task<DiscoveryResult> search = finder.FindToolSetsAsync(Query());
        await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TestContext.Current.CancellationToken);
        release.SetResult();
        Assert.Equal(2, (await search).ToolSets.Count);
        await finder.FindToolSetsAsync(Query());
        finder.FindToolSets(Query());
        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task SimultaneousIdenticalSearchesAreNotCoalesced()
    {
        TaskCompletionSource bothStarted = Signal();
        TaskCompletionSource release = Signal();
        int calls = 0;
        var provider = new TestProvider([Kind.Gnu], async (_, _, token) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                bothStarted.SetResult();
            }

            await release.Task.WaitAsync(token);
            return new DiscoveryResult();
        });
        var finder = new Finder([provider]);
        Task<DiscoveryResult> first = finder.FindToolSetsAsync(Query());
        Task<DiscoveryResult> second = finder.FindToolSetsAsync(Query());
        await bothStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task EnvironmentAndResultCollectionsAreSnapshots()
    {
        var environment = new Dictionary<string, string?> { ["CC"] = "original", ["EMPTY"] = "", ["UNSET"] = null };
        TaskCompletionSource started = Signal();
        TaskCompletionSource release = Signal();
        var candidates = new List<ToolSet> { new TestToolSet("one") };
        var sourceResult = new DiscoveryResult(candidates);
        candidates.Clear();
        IReadOnlyDictionary<string, string?>? observed = null;
        var finder = new Finder([new TestProvider([Kind.Gnu], async (_, context, token) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            observed = context.Environment;
            return sourceResult;
        })]);

        Task<DiscoveryResult> search = finder.FindToolSetsAsync(Query() with { Environment = environment });
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        environment["CC"] = "changed";
        release.SetResult();
        DiscoveryResult result = await search;
        Assert.Equal("original", observed!["CC"]);
        Assert.Equal("", observed["EMPTY"]);
        Assert.Null(observed["UNSET"]);
        Assert.Single(result.ToolSets);
        if (result.ToolSets is IList<ToolSet> mutableToolSets)
        {
            Assert.Throws<NotSupportedException>(() => mutableToolSets.Clear());
        }

        if (observed is IDictionary<string, string?> mutableEnvironment)
        {
            Assert.Throws<NotSupportedException>(() => mutableEnvironment.Clear());
        }
    }

    [Fact]
    public async Task ProviderFailuresRemainDiagnosticWhileOtherProvidersSucceed()
    {
        var broken = new TestProvider([Kind.Gnu], (_, _, _) => throw new IOException("unavailable"));
        var healthy = new TestProvider([Kind.Gnu], (_, _, _) => Task.FromResult(new DiscoveryResult([new TestToolSet("healthy")])));
        DiscoveryResult result = await new Finder([broken, healthy]).FindToolSetsAsync(Query());
        Assert.Single(result.ToolSets);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationReachesProvidersAndToolLookup()
    {
        using var cancellation = new CancellationTokenSource();
        TaskCompletionSource started = Signal();
        var finder = new Finder([new TestProvider([Kind.Gnu], async (_, _, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new DiscoveryResult();
        })]);
        Task<DiscoveryResult> search = finder.FindToolSetsAsync(Query(), cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        Assert.ThrowsAny<OperationCanceledException>(() => new TestToolSet("cancel").FindTool("gcc", cancellationToken: cancellation.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidTimeoutIsRejectedBeforeProvidersRun(int milliseconds)
    {
        var provider = new TestProvider([Kind.Gnu], (_, _, _) => throw new Xunit.Sdk.XunitException("Provider must not run."));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new Finder([provider]).FindToolSetsAsync(Query() with { ProbeTimeout = TimeSpan.FromMilliseconds(milliseconds) }));
    }

    [Fact]
    public void VersionConstraintsRejectInvalidRangesAndDoNotMatchUnknownVersions()
    {
        Assert.Throws<ArgumentException>(() => new VersionConstraint());
        Assert.Throws<ArgumentException>(() => new VersionConstraint(exact: new Version(1, 0), minimumInclusive: new Version(1, 0)));
        Assert.Throws<ArgumentException>(() => new VersionConstraint(minimumInclusive: new Version(2, 0), maximumExclusive: new Version(2, 0)));
        var range = new VersionConstraint(minimumInclusive: new Version(2, 0), maximumExclusive: new Version(3, 0));
        Assert.True(range.Matches(new Version(2, 0)));
        Assert.False(range.Matches(new Version(3, 0)));
        Assert.False(range.Matches(null));
    }

    private static Finder CreateFinder(params ToolSet[] toolSets) => new(
        [new TestProvider(Enum.GetValues<Kind>(), (_, _, _) => Task.FromResult(new DiscoveryResult(toolSets)))]);

    private static ToolSetQuery Query() => new() { Environment = new Dictionary<string, string?>() };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string Root(string name) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Incant.UnitTest.Core", "CppFind", name));

    private sealed class TestProvider(
        IReadOnlyCollection<Kind> kinds,
        Func<ToolSetQuery, DiscoveryContext, CancellationToken, Task<DiscoveryResult>> discover) : IDiscoveryProvider
    {
        public string Name => "Controlled provider";

        public IReadOnlyCollection<Kind> Kinds { get; } = kinds;

        public Task<DiscoveryResult> DiscoverAsync(ToolSetQuery query, DiscoveryContext context, CancellationToken cancellationToken) =>
            discover(query, context, cancellationToken);
    }

    private sealed class TestToolSet : ToolSet
    {
        private readonly Func<string, ToolQuery, CancellationToken, Task<Tool?>> _find;

        internal TestToolSet(string name, Version? version = null, Source source = Source.Path,
            Channel channel = Channel.Stable, Kind kind = Kind.Gnu, string? triple = null,
            Version? productVersion = null, Version? compilerVersion = null,
            Func<string, ToolQuery, CancellationToken, Task<Tool?>>? find = null)
            : base(kind, Root(name), Root(name), version ?? new Version(1, 0), productVersion,
                compilerVersion, defaultTargetTriple: triple, channel: channel, sources: [source])
        {
            _find = find ?? ((_, _, _) => Task.FromResult<Tool?>(null));
        }

        protected override Task<Tool?> FindToolCoreAsync(string name, ToolQuery query, CancellationToken cancellationToken) =>
            _find(name, query, cancellationToken);
    }
}
