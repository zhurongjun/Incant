using Incant.Base;
using Incant.Core.Toolchains;

namespace Incant.UnitTest.Core.Toolchains;

#pragma warning disable xUnit1051 // Provider doubles must observe the token supplied by the discovery API.

public sealed class DiscoveryServiceTests
{
    [Fact]
    public async Task DiscoveryAggregatesSelectedProvidersInParallel()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new DelegateProvider(
            "First",
            [Kind.Gnu],
            async cancellationToken =>
            {
                firstStarted.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Result(CreateToolchain(Kind.Gnu, Source.Path));
            });
        var second = new DelegateProvider(
            "Second",
            [Kind.Llvm],
            async cancellationToken =>
            {
                secondStarted.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Result(CreateToolchain(Kind.Llvm, Source.Path));
            });
        var service = new DiscoveryService([first, second]);

        Task<Catalog> discovery = service.DiscoverAsync(new DiscoveryOptions
        {
            Environment = s_emptyEnvironment,
        });
        await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        release.SetResult();
        Catalog catalog = await discovery;

        Assert.Equal(2, catalog.Installations.Count);
    }

    [Fact]
    public async Task KindFilterSkipsUnrelatedProviders()
    {
        var gnu = new CountingProvider(Kind.Gnu);
        var llvm = new CountingProvider(Kind.Llvm);
        var service = new DiscoveryService([gnu, llvm]);

        Catalog catalog = await service.DiscoverAsync(new DiscoveryOptions
        {
            Kinds = [Kind.Gnu],
            Environment = s_emptyEnvironment,
        });

        Assert.Equal(1, gnu.InvocationCount);
        Assert.Equal(0, llvm.InvocationCount);
        Assert.Single(catalog.Installations);
        Assert.Equal(Kind.Gnu, catalog.Installations[0].Kind);
    }

    [Fact]
    public async Task DuplicateInstallationsMergeSourcesByPriority()
    {
        string root = CreateRoot("duplicate");
        Installation pathInstallation = CreateToolchain(
            Kind.Gnu,
            Source.Path,
            root);
        Installation explicitInstallation = CreateToolchain(
            Kind.Gnu,
            Source.Explicit,
            root);
        var service = new DiscoveryService(
        [
            new StaticProvider("Path", Kind.Gnu, Result(pathInstallation)),
            new StaticProvider("Explicit", Kind.Gnu, Result(explicitInstallation)),
        ]);

        Catalog catalog = await service.DiscoverAsync(Options());

        Installation installation = Assert.Single(catalog.Installations);
        Assert.Equal(
            [Source.Explicit, Source.Path],
            installation.Sources);
        Assert.Equal(Platform.OS, installation.HostOS);
        Assert.Equal(TargetArchitecture.X64, installation.HostArchitecture);
    }

    [Fact]
    public async Task CoLocatedCompilerVersionsRemainSeparateInstallations()
    {
        string root = CreateRoot("co-located-versions");
        Installation version12 = CreateToolchain(
            Kind.Gnu,
            Source.StandardPath,
            root,
            version: new Version(12, 0));
        Installation version14 = CreateToolchain(
            Kind.Gnu,
            Source.StandardPath,
            root,
            version: new Version(14, 0));
        var service = new DiscoveryService(
        [
            new StaticProvider("GNU", Kind.Gnu, new DiscoveryResult(
                [version12, version14])),
        ]);

        Catalog catalog = await service.DiscoverAsync(Options());

        Assert.Equal(2, catalog.Installations.Count);
        Assert.Equal(
            [new Version(14, 0), new Version(12, 0)],
            catalog.Installations.Select(toolchain => toolchain.CompilerVersion));
        Assert.Equal(2, catalog.Profiles.Count);

        Profile selected = Resolver.Resolve(
            catalog,
            new Selection
            {
                Kind = Kind.Gnu,
                CompilerVersion = new VersionConstraint(exact: new Version(12, 0)),
            });
        Assert.Equal(new Version(12, 0), selected.Installation.CompilerVersion);
    }

    [Fact]
    public async Task PreferredExplicitCandidateRetainsVersionReportedByVendorCandidate()
    {
        string root = CreateRoot("explicit-version");
        var explicitInstallation = new Installation(
            Kind.VisualStudio,
            CompilerFamily.Msvc,
            root,
            PlatformOS.Windows,
            TargetArchitecture.X64,
            productVersion: null,
            compilerVersion: null,
            Channel.Stable,
            [Source.Explicit],
            [TargetPlatform.Windows],
            [TargetArchitecture.X64],
            []);
        var vendorInstallation = new Installation(
            Kind.VisualStudio,
            CompilerFamily.Msvc,
            root,
            PlatformOS.Windows,
            TargetArchitecture.X64,
            new Version(17, 9),
            new Version(14, 39),
            Channel.Stable,
            [Source.Vendor],
            [TargetPlatform.Windows],
            [TargetArchitecture.X64],
            []);
        var service = new DiscoveryService(
        [
            new StaticProvider("Explicit", Kind.VisualStudio, Result(explicitInstallation)),
            new StaticProvider("Vendor", Kind.VisualStudio, Result(vendorInstallation)),
        ]);

        Catalog catalog = await service.DiscoverAsync(Options());

        Installation installation = Assert.Single(catalog.Installations);
        Assert.Equal(new Version(17, 9), installation.ProductVersion);
        Assert.Equal(new Version(14, 39), installation.CompilerVersion);
        Assert.Equal(
            [Source.Explicit, Source.Vendor],
            installation.Sources);
    }

    [Fact]
    public async Task TrailingDirectorySeparatorsDoNotCreateDuplicateInstallations()
    {
        string root = CreateRoot("trailing");
        Installation first = CreateToolchain(
            Kind.Gnu,
            Source.StandardPath,
            root);
        Installation second = CreateToolchain(
            Kind.Gnu,
            Source.Path,
            root + Path.DirectorySeparatorChar);
        var service = new DiscoveryService(
        [
            new StaticProvider("First", Kind.Gnu, Result(first)),
            new StaticProvider("Second", Kind.Gnu, Result(second)),
        ]);

        Catalog catalog = await service.DiscoverAsync(Options());

        Assert.Single(catalog.Installations);
    }

    [Fact]
    public async Task PreviewInstallationsRequireExplicitOptIn()
    {
        Installation preview = CreateToolchain(
            Kind.Gnu,
            Source.Path,
            channel: Channel.Preview);
        var service = new DiscoveryService(
        [
            new StaticProvider("Preview", Kind.Gnu, Result(preview)),
        ]);

        Catalog stableCatalog = await service.DiscoverAsync(Options());
        Catalog previewCatalog = await service.DiscoverAsync(new DiscoveryOptions
        {
            IncludePreview = true,
            Environment = s_emptyEnvironment,
        });

        Assert.Empty(stableCatalog.Installations);
        Assert.Single(previewCatalog.Installations);
    }

    [Fact]
    public async Task CompatibleToolchainAndSdkCreateProfilesForSharedArchitectures()
    {
        Installation toolchain = CreateToolchain(
            Kind.VisualStudio,
            Source.Vendor,
            architectures: [TargetArchitecture.X64, TargetArchitecture.ARM64]);
        SdkInstallation sdk = CreateSdk(
            Kind.WindowsSdk,
            TargetPlatform.Windows,
            [TargetArchitecture.ARM64]);
        var service = new DiscoveryService(
        [
            new StaticProvider(
                "Windows",
                [Kind.VisualStudio, Kind.WindowsSdk],
                new DiscoveryResult([toolchain], [sdk])),
        ]);

        Catalog catalog = await service.DiscoverAsync(Options());

        Profile profile = Assert.Single(catalog.Profiles);
        Assert.Equal(toolchain.RootPath, profile.Installation.RootPath);
        Assert.NotNull(profile.Sdk);
        Assert.Equal(sdk.RootPath, profile.Sdk.RootPath);
        Assert.Equal(TargetArchitecture.ARM64, profile.TargetArchitecture);
        Assert.Equal("aarch64-pc-windows-msvc", profile.TargetTriple);
    }

    [Fact]
    public async Task WindowsLlvmRequiresAndPairsWithAWindowsSdk()
    {
        Installation toolchain = CreateToolchain(
            Kind.Llvm,
            Source.StandardPath,
            architectures: [TargetArchitecture.X64],
            platform: TargetPlatform.Windows);
        SdkInstallation sdk = CreateSdk(
            Kind.WindowsSdk,
            TargetPlatform.Windows,
            [TargetArchitecture.X64]);
        var service = new DiscoveryService(
        [
            new StaticProvider(
                "Windows LLVM",
                [Kind.Llvm, Kind.WindowsSdk],
                new DiscoveryResult([toolchain], [sdk])),
        ]);

        Catalog catalog = await service.DiscoverAsync(Options());

        Profile profile = Assert.Single(catalog.Profiles);
        Assert.Equal(Kind.Llvm, profile.Installation.Kind);
        Assert.Equal(Kind.WindowsSdk, profile.Sdk?.Kind);
    }

    [Fact]
    public async Task MacLlvmRequiresAndPairsWithTheMacXcodeSdk()
    {
        string xcodeRoot = CreateRoot("xcode");
        Installation llvm = CreateToolchain(
            Kind.Llvm,
            Source.StandardPath,
            platform: TargetPlatform.MacOS,
            architectures: [TargetArchitecture.ARM64]);
        Installation xcode = CreateToolchain(
            Kind.Xcode,
            Source.Vendor,
            root: xcodeRoot,
            platform: TargetPlatform.MacOS,
            architectures: [TargetArchitecture.ARM64]);
        SdkInstallation sdk = CreateSdk(
            Kind.Xcode,
            TargetPlatform.MacOS,
            [TargetArchitecture.ARM64],
            xcodeRoot);
        var service = new DiscoveryService(
        [
            new StaticProvider(
                "Apple LLVM",
                [Kind.Llvm, Kind.Xcode],
                new DiscoveryResult([llvm, xcode], [sdk])),
        ]);

        Catalog catalog = await service.DiscoverAsync(Options());

        Profile profile = Assert.Single(catalog.Profiles, profile =>
            profile.Installation.Kind == Kind.Llvm);
        Assert.Equal(Kind.Xcode, profile.Sdk?.Kind);
        Assert.Equal(TargetPlatform.MacOS, profile.TargetPlatform);
    }

    [Fact]
    public async Task IncompatibleSdkArchitectureDoesNotCreateAProfile()
    {
        Installation toolchain = CreateToolchain(
            Kind.VisualStudio,
            Source.Vendor,
            architectures: [TargetArchitecture.X64]);
        SdkInstallation sdk = CreateSdk(
            Kind.WindowsSdk,
            TargetPlatform.Windows,
            [TargetArchitecture.ARM64]);
        var service = new DiscoveryService(
        [
            new StaticProvider(
                "Windows",
                [Kind.VisualStudio, Kind.WindowsSdk],
                new DiscoveryResult([toolchain], [sdk])),
        ]);

        Catalog catalog = await service.DiscoverAsync(Options());

        Assert.Empty(catalog.Profiles);
    }

    [Fact]
    public async Task AndroidProfilesUseCanonicalArmTargetTriples()
    {
        Installation toolchain = CreateToolchain(
            Kind.AndroidNdk,
            Source.Environment,
            architectures:
            [
                TargetArchitecture.ARM,
                TargetArchitecture.ARM64,
            ],
            platform: TargetPlatform.Android);
        SdkInstallation sdk = CreateSdk(
            Kind.AndroidNdk,
            TargetPlatform.Android,
            [
                TargetArchitecture.ARM,
                TargetArchitecture.ARM64,
            ],
            toolchain.RootPath);
        var service = new DiscoveryService(
        [
            new StaticProvider(
                "Android",
                Kind.AndroidNdk,
                new DiscoveryResult([toolchain], [sdk])),
        ]);

        Catalog catalog = await service.DiscoverAsync(Options());

        Assert.Contains(catalog.Profiles, profile =>
            profile.TargetArchitecture == TargetArchitecture.ARM
            && profile.TargetTriple == "armv7a-linux-androideabi");
        Assert.Contains(catalog.Profiles, profile =>
            profile.TargetArchitecture == TargetArchitecture.ARM64
            && profile.TargetTriple == "aarch64-linux-android");
    }

    [Fact]
    public async Task SdkOwnedByOneInstallationIsNotPairedWithAnother()
    {
        string firstRoot = CreateRoot("android-first");
        string secondRoot = CreateRoot("android-second");
        Installation firstToolchain = CreateToolchain(
            Kind.AndroidNdk,
            Source.Environment,
            root: firstRoot,
            platform: TargetPlatform.Android);
        Installation secondToolchain = CreateToolchain(
            Kind.AndroidNdk,
            Source.StandardPath,
            root: secondRoot,
            platform: TargetPlatform.Android);
        SdkInstallation firstSdk = CreateSdk(
            Kind.AndroidNdk,
            TargetPlatform.Android,
            [TargetArchitecture.X64],
            firstRoot);
        SdkInstallation secondSdk = CreateSdk(
            Kind.AndroidNdk,
            TargetPlatform.Android,
            [TargetArchitecture.X64],
            secondRoot);
        var service = new DiscoveryService(
        [
            new StaticProvider(
                "Android",
                Kind.AndroidNdk,
                new DiscoveryResult(
                    [firstToolchain, secondToolchain],
                    [firstSdk, secondSdk])),
        ]);

        Catalog catalog = await service.DiscoverAsync(Options());

        Assert.Equal(2, catalog.Profiles.Count);
        Assert.All(catalog.Profiles, profile =>
            Assert.Equal(profile.Installation.RootPath, profile.Sdk?.RootPath));
    }

    [Fact]
    public async Task ExplicitPathMustResolveToRequestedFamily()
    {
        string root = CreateRoot("missing");
        var service = new DiscoveryService(
        [
            new StaticProvider("Empty", Kind.Gnu, new DiscoveryResult()),
        ]);

        DiscoveryException exception = await Assert.ThrowsAsync<DiscoveryException>(
            () => service.DiscoverAsync(new DiscoveryOptions
            {
                ExplicitPaths = [new PathHint(Kind.Gnu, root)],
                Environment = s_emptyEnvironment,
            }));

        Assert.Contains(root, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticProviderFailureBecomesDiagnosticAndOtherProvidersContinue()
    {
        var failing = new DelegateProvider(
            "Broken",
            [Kind.Gnu],
            _ => throw new IOException("unavailable"));
        var working = new StaticProvider(
            "Working",
            Kind.Llvm,
            Result(CreateToolchain(Kind.Llvm, Source.Path)));
        var service = new DiscoveryService([failing, working]);

        Catalog catalog = await service.DiscoverAsync(Options());

        Assert.Single(catalog.Installations);
        Diagnostic diagnostic = Assert.Single(catalog.Diagnostics);
        Assert.Equal("provider-failed", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public async Task CachedDiscoveryCanBeRefreshedOrCleared()
    {
        var provider = new CountingProvider(Kind.Gnu);
        var service = new DiscoveryService([provider]);

        Catalog first = await service.DiscoverAsync(Options());
        Catalog second = await service.DiscoverAsync(Options());
        Catalog refreshed = await service.DiscoverAsync(new DiscoveryOptions
        {
            Refresh = true,
            Environment = s_emptyEnvironment,
        });
        service.ClearCache();
        Catalog afterClear = await service.DiscoverAsync(Options());

        Assert.Same(first, second);
        Assert.NotSame(second, refreshed);
        Assert.NotSame(refreshed, afterClear);
        Assert.Equal(3, provider.InvocationCount);
    }

    [Fact]
    public async Task EnvironmentChangesCreateIndependentCacheEntries()
    {
        var provider = new CountingProvider(Kind.Gnu);
        var service = new DiscoveryService([provider]);

        await service.DiscoverAsync(new DiscoveryOptions
        {
            Environment = new Dictionary<string, string?> { ["CC"] = "gcc-12" },
        });
        await service.DiscoverAsync(new DiscoveryOptions
        {
            Environment = new Dictionary<string, string?> { ["CC"] = "gcc-14" },
        });

        Assert.Equal(2, provider.InvocationCount);
    }

    [Fact]
    public async Task EnvironmentCacheKeyPreservesNullEmptyAndDelimitedValues()
    {
        var provider = new CountingProvider(Kind.Gnu);
        var service = new DiscoveryService([provider]);

        await service.DiscoverAsync(new DiscoveryOptions
        {
            Environment = new Dictionary<string, string?> { ["CC"] = null },
        });
        await service.DiscoverAsync(new DiscoveryOptions
        {
            Environment = new Dictionary<string, string?> { ["CC"] = string.Empty },
        });
        await service.DiscoverAsync(new DiscoveryOptions
        {
            Environment = new Dictionary<string, string?> { ["A"] = "x|B=y" },
        });
        await service.DiscoverAsync(new DiscoveryOptions
        {
            Environment = new Dictionary<string, string?>
            {
                ["A"] = "x",
                ["B"] = "y",
            },
        });

        Assert.Equal(4, provider.InvocationCount);
    }

    [Fact]
    public async Task OptionsCollectionsAreSnapshottedBeforeProviderExecution()
    {
        var kinds = new List<Kind> { Kind.Gnu };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyCollection<Kind>? observedKinds = null;
        var provider = new DelegateProvider(
            "Snapshot",
            [Kind.Gnu],
            async (context, cancellationToken) =>
            {
                started.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                observedKinds = context.Options.Kinds;
                return new DiscoveryResult();
            });
        var service = new DiscoveryService([provider]);

        Task<Catalog> discovery = service.DiscoverAsync(new DiscoveryOptions
        {
            Kinds = kinds,
            Environment = s_emptyEnvironment,
        });
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        kinds.Add(Kind.Llvm);
        release.SetResult();
        await discovery;

        Assert.Equal([Kind.Gnu], observedKinds);
    }

    [Fact]
    public async Task NonPositiveProbeTimeoutIsRejectedBeforeProvidersRun()
    {
        var provider = new CountingProvider(Kind.Gnu);
        var service = new DiscoveryService([provider]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.DiscoverAsync(new DiscoveryOptions
            {
                ProbeTimeout = TimeSpan.Zero,
                Environment = s_emptyEnvironment,
            }));

        Assert.Equal(0, provider.InvocationCount);
    }

    [Fact]
    public async Task CallerCancellationReachesProvider()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelegateProvider(
            "Wait",
            [Kind.Gnu],
            async cancellationToken =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new DiscoveryResult();
            });
        var service = new DiscoveryService([provider]);
        using var cancellation = new CancellationTokenSource();

        Task<Catalog> discovery = service.DiscoverAsync(Options(), cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => discovery);
    }

    private static DiscoveryOptions Options() => new()
    {
        Environment = s_emptyEnvironment,
    };

    private static DiscoveryResult Result(Installation installation) => new([installation]);

    private static Installation CreateToolchain(
        Kind kind,
        Source source,
        string? root = null,
        Channel channel = Channel.Stable,
        IReadOnlyList<TargetArchitecture>? architectures = null,
        TargetPlatform? platform = null,
        Version? version = null)
    {
        TargetPlatform resolvedPlatform = platform ?? (kind == Kind.VisualStudio
            ? TargetPlatform.Windows
            : TargetPlatform.Linux);
        return new Installation(
            kind,
            kind == Kind.Gnu ? CompilerFamily.Gcc : CompilerFamily.Clang,
            root ?? CreateRoot(kind.ToString()),
            Platform.OS,
            TargetArchitecture.X64,
            version ?? new Version(1, 0),
            version ?? new Version(1, 0),
            channel,
            [source],
            [resolvedPlatform],
            architectures ?? [TargetArchitecture.X64],
            []);
    }

    private static SdkInstallation CreateSdk(
        Kind kind,
        TargetPlatform platform,
        IReadOnlyList<TargetArchitecture> architectures,
        string? root = null)
    {
        string sdkRoot = root ?? CreateRoot("sdk");
        return new SdkInstallation(
            kind,
            platform,
            sdkRoot,
            sdkRoot,
            new Version(1, 0),
            [Source.Vendor],
            architectures);
    }

    private static string CreateRoot(string name) => Path.Combine(
        Path.GetTempPath(),
        "Incant.UnitTest.Core",
        name);

    private sealed class StaticProvider : IDiscoveryProvider
    {
        private readonly DiscoveryResult _result;

        internal StaticProvider(string name, Kind kind, DiscoveryResult result)
            : this(name, [kind], result)
        {
        }

        internal StaticProvider(
            string name,
            IReadOnlyCollection<Kind> kinds,
            DiscoveryResult result)
        {
            Name = name;
            Kinds = kinds;
            _result = result;
        }

        public string Name { get; }

        public IReadOnlyCollection<Kind> Kinds { get; }

        public ValueTask<DiscoveryResult> DiscoverAsync(
            DiscoveryContext context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(_result);
    }

    private sealed class CountingProvider : IDiscoveryProvider
    {
        private readonly Kind _kind;

        internal CountingProvider(Kind kind)
        {
            _kind = kind;
            Kinds = Array.AsReadOnly([kind]);
        }

        internal int InvocationCount { get; private set; }

        public string Name => "Counting";

        public IReadOnlyCollection<Kind> Kinds { get; }

        public ValueTask<DiscoveryResult> DiscoverAsync(
            DiscoveryContext context,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return ValueTask.FromResult(Result(CreateToolchain(_kind, Source.Path)));
        }
    }

    private sealed class DelegateProvider : IDiscoveryProvider
    {
        private readonly Func<
            DiscoveryContext,
            CancellationToken,
            Task<DiscoveryResult>> _callback;

        internal DelegateProvider(
            string name,
            IReadOnlyCollection<Kind> kinds,
            Func<CancellationToken, Task<DiscoveryResult>> callback)
            : this(name, kinds, (_, cancellationToken) => callback(cancellationToken))
        {
        }

        internal DelegateProvider(
            string name,
            IReadOnlyCollection<Kind> kinds,
            Func<DiscoveryContext, CancellationToken, Task<DiscoveryResult>> callback)
        {
            Name = name;
            Kinds = kinds;
            _callback = callback;
        }

        public string Name { get; }

        public IReadOnlyCollection<Kind> Kinds { get; }

        public async ValueTask<DiscoveryResult> DiscoverAsync(
            DiscoveryContext context,
            CancellationToken cancellationToken = default) =>
            await _callback(context, cancellationToken);
    }

    private static readonly IReadOnlyDictionary<string, string?> s_emptyEnvironment =
        new Dictionary<string, string?>();
}
