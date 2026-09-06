using Incant.Core.Cpp;
using Incant.Core.Cpp.FindSdk;

namespace Incant.UnitTest.Core.Cpp.FindSdk;

#pragma warning disable xUnit1051 // Provider doubles must observe the token supplied by the public API.

public sealed class FinderTests
{
    [Fact]
    public async Task PlatformAndCompilerFilesRemainSeparateIncludingExternalReferences()
    {
        string platformPath = Root("apple-sdk");
        var platform = new Sdk(Kind.Apple, platformPath,
            [new TargetLayout(TargetPlatform.MacOS, TargetArchitecture.ARM64,
                [new Resource(ResourcePurpose.CInclude, Path.Combine(platformPath, "usr", "include"))])],
            new Version(15, 5));
        var compiler = new Sdk(Kind.AppleClang, Root("clang"),
            [new TargetLayout(TargetPlatform.MacOS, TargetArchitecture.ARM64,
                [new Resource(ResourcePurpose.CInclude, Path.Combine(platformPath, "usr", "include"), isExternal: true)])],
            new Version(17, 0));
        DiscoveryResult result = await CreateFinder(platform, compiler).FindSdksAsync(Query());

        Assert.Equal(2, result.Sdks.Count);
        Assert.False(platform.Layouts[0].Resources[0].IsExternal);
        Assert.True(compiler.Layouts[0].Resources[0].IsExternal);
        Assert.Equal(platform.Layouts[0].Resources[0].Path, compiler.Layouts[0].Resources[0].Path);
        Assert.Equal(new Version(15, 5), result.Sdks.Single(sdk => sdk.Kind == Kind.Apple).Version);
    }

    [Fact]
    public async Task LanguageSearchOrderAndFrameworkClassificationArePreserved()
    {
        Resource[] resources =
        [
            new(ResourcePurpose.CppInclude, Root("z-stdlib")),
            new(ResourcePurpose.CInclude, Root("builtin")),
            new(ResourcePurpose.CppInclude, Root("builtin")),
            new(ResourcePurpose.CInclude, Root("a-system"), true),
            new(ResourcePurpose.CppInclude, Root("a-system"), true),
            new(ResourcePurpose.Framework, Root("Frameworks"), true),
        ];
        Sdk sdk = CreateSdk(resources: resources);
        Sdk result = (await CreateFinder(sdk).FindSdkAsync(Query()))!;

        Assert.Equal(resources, result.Layouts[0].Resources);
        Assert.Equal([Root("z-stdlib"), Root("builtin"), Root("a-system")],
            result.Layouts[0].Resources.Where(resource => resource.Purpose == ResourcePurpose.CppInclude).Select(resource => resource.Path));
        Assert.Single(result.Layouts[0].Resources, resource => resource.Purpose == ResourcePurpose.Framework);
    }

    [Fact]
    public async Task AndroidApiAvailabilityIsNotUnionedAcrossArchitectures()
    {
        var sdk = new Sdk(Kind.AndroidNdk, Root("ndk"),
        [
            new TargetLayout(TargetPlatform.Android, TargetArchitecture.ARM64, apiLevels: [21, 24, 35],
                apiAliases: new Dictionary<int, int> { [22] = 21 }),
            new TargetLayout(TargetPlatform.Android, TargetArchitecture.X64, apiLevels: [24, 35]),
        ], new Version(27, 2));
        Finder finder = CreateFinder(sdk);

        Assert.Null(await finder.FindSdkAsync(Query() with { TargetArchitecture = TargetArchitecture.X64, AndroidApi = 21 }));
        Assert.NotNull(await finder.FindSdkAsync(Query() with { TargetArchitecture = TargetArchitecture.ARM64, AndroidApi = 22 }));
        TargetLayout selected = Assert.Single((await finder.FindSdkAsync(Query() with
        {
            TargetArchitecture = TargetArchitecture.ARM64,
            AndroidApi = 21,
        }))!.Layouts);
        Assert.Equal([21, 24, 35], selected.ApiLevels);
        Assert.Equal(new Version(27, 2), sdk.Version);
    }

    [Fact]
    public async Task Arm64SimulatorIsDistinctFromDeviceAndUnknownSupportDoesNotMatch()
    {
        var sdk = new Sdk(Kind.Apple, Root("apple"),
        [
            new TargetLayout(TargetPlatform.IOS, TargetArchitecture.ARM64),
            new TargetLayout(TargetPlatform.IOSSimulator, TargetArchitecture.ARM64),
            new TargetLayout(TargetPlatform.MacOS, TargetArchitecture.Unknown),
        ]);
        Finder finder = CreateFinder(sdk);

        Sdk simulator = (await finder.FindSdkAsync(Query() with { TargetPlatform = TargetPlatform.IOSSimulator }))!;
        Assert.Equal(TargetPlatform.IOSSimulator, Assert.Single(simulator.Layouts).Platform);
        Assert.Null(await finder.FindSdkAsync(Query() with { TargetPlatform = TargetPlatform.MacOS, TargetArchitecture = TargetArchitecture.ARM64 }));
    }

    [Fact]
    public async Task TripleFilteringDoesNotBorrowAnotherTargetLayout()
    {
        var sdk = new Sdk(Kind.Sysroot, Root("cross"),
        [
            new TargetLayout(TargetPlatform.Linux, TargetArchitecture.ARM64, targetTriple: "aarch64-linux-gnu"),
            new TargetLayout(TargetPlatform.Linux, TargetArchitecture.X64, targetTriple: "x86_64-linux-gnu"),
        ]);
        Sdk? result = await CreateFinder(sdk).FindSdkAsync(Query() with { TargetTriple = "aarch64-linux-gnu" });
        Assert.Equal(TargetArchitecture.ARM64, Assert.Single(result!.Layouts).Architecture);
    }

    [Theory]
    [InlineData("wasm32-wasi", "wasm32-wasip1")]
    [InlineData("wasm32-unknown-wasi", "wasm32-wasip1")]
    [InlineData("wasm32-wasi-threads", "wasm32-wasip1-threads")]
    public async Task WasiPreviewOneLegacyTripleAliasesMatchModernSpelling(string requested, string installed)
    {
        var sdk = new Sdk(Kind.WasiSdk, Root("wasi"),
            [new TargetLayout(TargetPlatform.Wasi, TargetArchitecture.Wasm32, targetTriple: installed)]);
        Sdk? result = await CreateFinder(sdk).FindSdkAsync(Query() with { TargetTriple = requested });
        Assert.Equal(installed, Assert.Single(result!.Layouts).TargetTriple);
    }

    [Theory]
    [InlineData("wasm32-wasip2")]
    [InlineData("wasm32-wasip3")]
    [InlineData("wasm32-wasip1-threads")]
    public async Task DistinctWasiTargetsDoNotMatchPreviewOne(string requested)
    {
        var sdk = new Sdk(Kind.WasiSdk, Root("wasi"),
            [new TargetLayout(TargetPlatform.Wasi, TargetArchitecture.Wasm32, targetTriple: "wasm32-wasip1")]);
        Assert.Null(await CreateFinder(sdk).FindSdkAsync(Query() with { TargetTriple = requested }));
    }

    [Fact]
    public async Task VersionUnknownRemainsUnknownAndDoesNotMatchVersionConstraint()
    {
        Finder finder = CreateFinder(CreateSdk(version: null));
        Assert.Null((await finder.FindSdkAsync(Query()))!.Version);
        Assert.Null(await finder.FindSdkAsync(Query() with { Version = new VersionConstraint(exact: new Version(1, 0)) }));
    }

    [Fact]
    public async Task SourcePriorityAndNumericalVersionSelectionAreIndependentOfEnumerationOrder()
    {
        Finder finder = CreateFinder(
            CreateSdk("v9", version: new Version(9, 0), source: Source.Vendor),
            CreateSdk("v10", version: new Version(10, 0), source: Source.Vendor),
            CreateSdk("active", version: new Version(8, 0), source: Source.Environment));
        Assert.Equal(new Version(8, 0), (await finder.FindSdkAsync(Query()))!.Version);
        Assert.Equal(new Version(10, 0), (await finder.FindSdkAsync(Query() with
        {
            Version = new VersionConstraint(minimumInclusive: new Version(9, 0)),
        }))!.Version);
    }

    [Fact]
    public async Task DuplicateSdkPathsMergeSourcesAndKeepDifferentComponentsAndVersions()
    {
        Finder finder = CreateFinder(
            CreateSdk("shared", version: new Version(1, 0), source: Source.Path),
            CreateSdk("shared" + Path.DirectorySeparatorChar, version: new Version(1, 0), source: Source.Explicit),
            CreateSdk("shared", version: new Version(2, 0)),
            CreateSdk("shared", kind: Kind.Llvm, version: new Version(1, 0)));
        DiscoveryResult result = await finder.FindSdksAsync(Query());
        Assert.Equal(3, result.Sdks.Count);
        Assert.Equal([Source.Explicit, Source.Path], result.Sdks.First(sdk => sdk.Sources.Contains(Source.Explicit)).Sources);
    }

    [Fact]
    public async Task PreviewIsOptInAndProductVersionDoesNotReplaceSdkVersion()
    {
        var sdk = new Sdk(Kind.Apple, Root("preview"), [new TargetLayout(TargetPlatform.MacOS, TargetArchitecture.ARM64)],
            new Version(26, 2), productVersion: new Version(26, 1), channel: Channel.Preview);
        Finder finder = CreateFinder(sdk);
        Assert.Null(await finder.FindSdkAsync(Query()));
        Assert.NotNull(await finder.FindSdkAsync(Query() with
        {
            IncludePreview = true,
            Version = new VersionConstraint(exact: new Version(26, 2)),
            ProductVersion = new VersionConstraint(exact: new Version(26, 1)),
        }));
    }

    [Fact]
    public async Task ExplicitInputErrorsRemainDifferentFromNormalNoMatch()
    {
        Finder finder = CreateFinder(CreateSdk("valid"));
        Assert.Null(await finder.FindSdkAsync(Query() with { Kind = Kind.Windows }));
        Assert.Empty((await finder.FindSdksAsync(Query() with { Kind = Kind.Windows })).Sdks);
        await Assert.ThrowsAsync<DiscoveryException>(() => finder.FindSdksAsync(Query() with { RootPath = Root("missing") }));
        await Assert.ThrowsAsync<DiscoveryException>(() => finder.FindSdksAsync(Query() with { CompilerPath = Root("missing-clang") }));
    }

    [Fact]
    public async Task ExplicitCompilerAndSysrootArePassedAsPathsWithoutToolSetCoupling()
    {
        string compiler = Root("bin/clang");
        string sysroot = Root("target");
        SdkQuery? observed = null;
        var provider = new TestProvider([Kind.Llvm], (query, _, _) =>
        {
            observed = query;
            return Task.FromResult(new DiscoveryResult(
                [new Sdk(Kind.Llvm, Root("compiler"),
                    [new TargetLayout(TargetPlatform.Android, TargetArchitecture.ARM64, sysrootPath: sysroot)],
                    compilerPath: compiler)]));
        });
        Sdk? sdk = await new Finder([provider]).FindSdkAsync(Query() with
        {
            CompilerPath = compiler,
            SysrootPath = sysroot,
        });
        Assert.Equal(compiler, observed!.CompilerPath);
        Assert.Equal(sysroot, observed.SysrootPath);
        Assert.Equal(compiler, sdk!.CompilerPath);
    }

    [Fact]
    public void ResourceAndLayoutCollectionsAreDefensiveReadOnlySnapshots()
    {
        var resources = new List<Resource> { new(ResourcePurpose.Library, Root("libSystem.tbd")) };
        var apis = new List<int> { 24, 21, 21 };
        var aliases = new Dictionary<int, int> { [22] = 21 };
        var layout = new TargetLayout(TargetPlatform.Android, TargetArchitecture.ARM64, resources,
            apiLevels: apis, apiAliases: aliases);
        var layouts = new List<TargetLayout> { layout };
        var sdk = new Sdk(Kind.AndroidNdk, Root("sdk"), layouts);
        resources.Clear();
        apis.Clear();
        aliases.Clear();
        layouts.Clear();

        Assert.Single(sdk.Layouts);
        Assert.False(Assert.Single(layout.Resources).IsDirectory);
        Assert.Equal([21, 24], layout.ApiLevels);
        Assert.Equal(21, layout.ApiAliases[22]);
        if (layout.Resources is IList<Resource> mutableResources)
        {
            Assert.Throws<NotSupportedException>(() => mutableResources.Clear());
        }

        if (layout.ApiAliases is IDictionary<int, int> mutableAliases)
        {
            Assert.Throws<NotSupportedException>(() => mutableAliases.Clear());
        }
    }

    [Fact]
    public async Task NoCacheAndNoRequestCoalescingEvenForConcurrentIdenticalQueries()
    {
        TaskCompletionSource bothStarted = Signal();
        TaskCompletionSource release = Signal();
        int calls = 0;
        var finder = new Finder([new TestProvider([Kind.Linux], async (_, _, token) =>
        {
            if (Interlocked.Increment(ref calls) >= 2)
            {
                bothStarted.TrySetResult();
            }

            await release.Task.WaitAsync(token);
            return new DiscoveryResult([CreateSdk()]);
        })]);

        Task<DiscoveryResult> first = finder.FindSdksAsync(Query());
        Task<DiscoveryResult> second = finder.FindSdksAsync(Query());
        await bothStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        release.SetResult();
        await Task.WhenAll(first, second);
        finder.FindSdks(Query());
        Assert.NotNull(finder.FindSdk(Query()));
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task SdkProvidersRunInParallelAndUnrelatedKindsAreSkipped()
    {
        TaskCompletionSource firstStarted = Signal();
        TaskCompletionSource secondStarted = Signal();
        TaskCompletionSource release = Signal();
        TestProvider Provider(TaskCompletionSource signal) => new([Kind.Linux], async (_, _, token) =>
        {
            signal.SetResult();
            await release.Task.WaitAsync(token);
            return new DiscoveryResult([CreateSdk()]);
        });
        var unrelated = new TestProvider([Kind.Windows], (_, _, _) => throw new Xunit.Sdk.XunitException("Unrelated provider ran."));
        var finder = new Finder([Provider(firstStarted), Provider(secondStarted), unrelated]);

        Task<DiscoveryResult> task = finder.FindSdksAsync(Query() with { Kind = Kind.Linux });
        await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TestContext.Current.CancellationToken);
        release.SetResult();
        Assert.Single((await task).Sdks);
    }

    [Fact]
    public async Task MultipleFailuresAreRetainedAndCancellationPropagates()
    {
        var first = new TestProvider([Kind.Linux], (_, _, _) => throw new IOException("first"));
        var second = new TestProvider([Kind.Linux], (_, _, _) => throw new IOException("second"));
        DiscoveryResult result = await new Finder([first, second]).FindSdksAsync(Query());
        Assert.Empty(result.Sdks);
        Assert.Equal(2, result.Diagnostics.Count);

        using var cancellation = new CancellationTokenSource();
        TaskCompletionSource started = Signal();
        var finder = new Finder([new TestProvider([Kind.Linux], async (_, _, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new DiscoveryResult();
        })]);
        Task<DiscoveryResult> task = finder.FindSdksAsync(Query(), cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task SdkEnvironmentIsFrozenBeforeProviderExecution()
    {
        var environment = new Dictionary<string, string?> { ["SDKROOT"] = "original" };
        TaskCompletionSource started = Signal();
        TaskCompletionSource release = Signal();
        string? observed = null;
        var finder = new Finder([new TestProvider([Kind.Apple], async (_, context, token) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            observed = context.GetEnvironmentVariable("SDKROOT");
            return new DiscoveryResult();
        })]);
        Task<DiscoveryResult> task = finder.FindSdksAsync(Query() with { Environment = environment });
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        environment["SDKROOT"] = "changed";
        release.SetResult();
        await task;
        Assert.Equal("original", observed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidApiLevelsAreRejected(int level) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CreateFinder().FindSdksAsync(Query() with { AndroidApi = level }));

    [Fact]
    public async Task MultilibVariantsRemainDistinctWithinOneCompilerSdk()
    {
        string root = Root("gcc");
        var first = new Sdk(Kind.Gnu, root,
            [new TargetLayout(TargetPlatform.Linux, TargetArchitecture.X64, multilib: ".")]);
        var second = new Sdk(Kind.Gnu, root,
            [new TargetLayout(TargetPlatform.Linux, TargetArchitecture.X64, multilib: "x32")]);
        Sdk sdk = Assert.Single((await CreateFinder(first, second).FindSdksAsync(Query())).Sdks);
        Assert.Equal([".", "x32"], sdk.Layouts.Select(layout => layout.Multilib));
    }

    [Fact]
    public void AndroidResourcesRetainTheirInstalledApiLevelAndAliasesMustBeBackedByFiles()
    {
        var resource = new Resource(ResourcePurpose.Library, Root("ndk/21/libc.so"), apiLevel: 21);
        Assert.Equal(21, resource.ApiLevel);
        Assert.Throws<ArgumentException>(() => new TargetLayout(TargetPlatform.Android, TargetArchitecture.ARM64,
            apiLevels: [21], apiAliases: new Dictionary<int, int> { [22] = 24 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Resource(ResourcePurpose.Library, Root("libc.so"), apiLevel: 0));
    }

    [Fact]
    public async Task ValidExplicitSdkWithUnmatchedVersionReturnsNull()
    {
        Finder finder = CreateFinder(CreateSdk("valid-version", version: new Version(1, 0)));
        Assert.Null(await finder.FindSdkAsync(Query() with
        {
            RootPath = Root("valid-version"),
            Version = new VersionConstraint(exact: new Version(2, 0)),
        }));
    }

    [Fact]
    public async Task MixedProviderOutputIsFilteredByRequestedSdkKind()
    {
        Finder finder = CreateFinder(CreateSdk(), CreateSdk("llvm", kind: Kind.Llvm));
        Assert.Equal(Kind.Llvm, Assert.Single((await finder.FindSdksAsync(Query() with { Kind = Kind.Llvm })).Sdks).Kind);
    }

    [Fact]
    public async Task PreCanceledSearchDoesNotInvokeProviders()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new TestProvider([Kind.Linux], (_, _, _) => throw new Xunit.Sdk.XunitException("Provider ran."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Finder([provider]).FindSdksAsync(Query(), cancellation.Token));
    }

    [Fact]
    public async Task InvalidTimeoutAndKindAreRejected()
    {
        Finder finder = CreateFinder();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => finder.FindSdksAsync(Query() with { ProbeTimeout = TimeSpan.Zero }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => finder.FindSdksAsync(Query() with { Kind = (Kind)int.MaxValue }));
    }

    [Fact]
    public void DeploymentMetadataDoesNotReplaceSdkOrProductVersions()
    {
        var sdk = new Sdk(Kind.Apple, Root("xcode-sdk"),
            [new TargetLayout(TargetPlatform.IOSSimulator, TargetArchitecture.ARM64,
                minimumDeploymentVersion: new Version(15, 0), defaultDeploymentVersion: new Version(26, 0))],
            new Version(26, 2), productVersion: new Version(26, 1));
        Assert.Equal(new Version(26, 2), sdk.Version);
        Assert.Equal(new Version(26, 1), sdk.ProductVersion);
        Assert.Equal(new Version(15, 0), sdk.Layouts[0].MinimumDeploymentVersion);
        Assert.Equal(new Version(26, 0), sdk.Layouts[0].DefaultDeploymentVersion);
    }

    private static Finder CreateFinder(params Sdk[] sdks) =>
        new([new TestProvider(Enum.GetValues<Kind>(), (_, _, _) => Task.FromResult(new DiscoveryResult(sdks)))]);

    private static Sdk CreateSdk(string name = "system", Kind kind = Kind.Linux, Version? version = null,
        Source source = Source.Path, IEnumerable<Resource>? resources = null) =>
        new(kind, Root(name), [new TargetLayout(TargetPlatform.Linux, TargetArchitecture.X64, resources)],
            version, sources: [source]);

    private static SdkQuery Query() => new() { Environment = new Dictionary<string, string?>() };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string Root(string name) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Incant.UnitTest.Core", "SdkFind", name));

    private sealed class TestProvider(
        IReadOnlyCollection<Kind> kinds,
        Func<SdkQuery, DiscoveryContext, CancellationToken, Task<DiscoveryResult>> discover) : IDiscoveryProvider
    {
        public string Name => "Controlled SDK provider";

        public IReadOnlyCollection<Kind> Kinds { get; } = kinds;

        public Task<DiscoveryResult> DiscoverAsync(SdkQuery query, DiscoveryContext context, CancellationToken cancellationToken) =>
            discover(query, context, cancellationToken);
    }
}
