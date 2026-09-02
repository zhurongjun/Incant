using Incant.Base;
using Incant.Core.Toolchains;

namespace Incant.UnitTest.Core.Toolchains;

public sealed class ResolverTests
{
    [Fact]
    public void ResolveSelectsHighestStableMatchingVersion()
    {
        Profile oldProfile = CreateProfile(new Version(12, 0), Channel.Stable);
        Profile newProfile = CreateProfile(new Version(14, 1), Channel.Stable);
        Profile previewProfile = CreateProfile(new Version(15, 0), Channel.Preview);
        var catalog = new Catalog(
            [oldProfile.Installation, newProfile.Installation, previewProfile.Installation],
            [],
            [oldProfile, newProfile, previewProfile]);

        Profile result = Resolver.Resolve(
            catalog,
            new Selection
            {
                Kind = Kind.Gnu,
                TargetPlatform = TargetPlatform.Linux,
                CompilerVersion = new VersionConstraint(
                    minimumInclusive: new Version(12, 0),
                    maximumExclusive: new Version(15, 0)),
            });

        Assert.Equal(new Version(14, 1), result.Installation.CompilerVersion);
    }

    [Fact]
    public void ResolveCanSelectPreviewWhenRequested()
    {
        Profile stable = CreateProfile(new Version(14, 0), Channel.Stable);
        Profile preview = CreateProfile(new Version(15, 0), Channel.Preview);
        var catalog = new Catalog(
            [stable.Installation, preview.Installation],
            [],
            [stable, preview]);

        Profile result = Resolver.Resolve(
            catalog,
            new Selection
            {
                Kind = Kind.Gnu,
                IncludePreview = true,
            });

        Assert.Equal(Channel.Preview, result.Installation.Channel);
        Assert.Equal(new Version(15, 0), result.Installation.CompilerVersion);
    }

    [Fact]
    public void ResolvePrefersHigherPrioritySourceBeforeVersion()
    {
        Profile explicitProfile = CreateProfile(
            new Version(12, 0),
            Channel.Stable,
            Source.Explicit);
        Profile pathProfile = CreateProfile(
            new Version(14, 0),
            Channel.Stable,
            Source.Path);
        var catalog = new Catalog(
            [explicitProfile.Installation, pathProfile.Installation],
            [],
            [explicitProfile, pathProfile]);

        Profile result = Resolver.Resolve(catalog, new Selection());

        Assert.Equal(Source.Explicit, result.Installation.Sources[0]);
        Assert.Equal(new Version(12, 0), result.Installation.ProductVersion);
    }

    [Fact]
    public void ResolvePrefersHigherPrioritySdkSourceBeforeSdkVersion()
    {
        string root = Path.Combine(Path.GetTempPath(), "Incant.UnitTest.Core", "llvm");
        var toolchain = new Installation(
            Kind.Llvm,
            CompilerFamily.Clang,
            root,
            Platform.OS,
            TargetArchitecture.ARM64,
            new Version(18, 1),
            new Version(18, 1),
            Channel.Stable,
            [Source.Path],
            [TargetPlatform.MacOS],
            [TargetArchitecture.ARM64],
            []);
        SdkInstallation activeSdk = CreateMacSdk(
            new Version(15, 5),
            Source.Environment);
        SdkInstallation newerSdk = CreateMacSdk(
            new Version(26, 2),
            Source.StandardPath);
        var activeProfile = new Profile(
            toolchain,
            activeSdk,
            TargetPlatform.MacOS,
            TargetArchitecture.ARM64,
            "arm64-apple-darwin");
        var newerProfile = new Profile(
            toolchain,
            newerSdk,
            TargetPlatform.MacOS,
            TargetArchitecture.ARM64,
            "arm64-apple-darwin");
        var catalog = new Catalog(
            [toolchain],
            [activeSdk, newerSdk],
            [activeProfile, newerProfile]);

        Profile result = Resolver.Resolve(
            catalog,
            new Selection
            {
                Kind = Kind.Llvm,
                TargetPlatform = TargetPlatform.MacOS,
            });

        Assert.Same(activeSdk, result.Sdk);
    }

    [Fact]
    public void ResolveReportsCatalogDiagnosticsWhenNoProfileMatches()
    {
        var diagnostic = new Diagnostic(
            DiagnosticSeverity.Warning,
            "not-installed",
            "Test",
            "Missing.");
        var catalog = new Catalog([], [], [], [diagnostic]);

        NotFoundException exception = Assert.Throws<NotFoundException>(
            () => Resolver.Resolve(
                catalog,
                new Selection
                {
                    TargetPlatform = TargetPlatform.Wasi,
                }));

        Diagnostic reportedDiagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal(diagnostic.Code, reportedDiagnostic.Code);
        Assert.Equal(diagnostic.Message, reportedDiagnostic.Message);
    }

    [Fact]
    public void VersionConstraintRejectsConflictingOrEmptyRanges()
    {
        Assert.Throws<ArgumentException>(() => new VersionConstraint());
        Assert.Throws<ArgumentException>(() => new VersionConstraint(
            exact: new Version(1, 0),
            minimumInclusive: new Version(1, 0)));
        Assert.Throws<ArgumentException>(() => new VersionConstraint(
            minimumInclusive: new Version(2, 0),
            maximumExclusive: new Version(2, 0)));
    }

    [Fact]
    public void VersionConstraintUsesInclusiveMinimumAndExclusiveMaximum()
    {
        var constraint = new VersionConstraint(
            minimumInclusive: new Version(2, 0),
            maximumExclusive: new Version(3, 0));

        Assert.True(constraint.Matches(new Version(2, 0)));
        Assert.True(constraint.Matches(new Version(2, 9)));
        Assert.False(constraint.Matches(new Version(3, 0)));
        Assert.False(constraint.Matches(null));
    }

    [Fact]
    public void InstallationRequiresAtLeastOneDiscoverySource()
    {
        Assert.Throws<ArgumentException>(() => new Installation(
            Kind.Gnu,
            CompilerFamily.Gcc,
            Path.GetTempPath(),
            Platform.OS,
            TargetArchitecture.X64,
            productVersion: null,
            compilerVersion: null,
            Channel.Stable,
            [],
            [TargetPlatform.Linux],
            [TargetArchitecture.X64],
            []));
    }

    private static Profile CreateProfile(
        Version version,
        Channel channel,
        Source source = Source.Path)
    {
        string root = Path.Combine(Path.GetTempPath(), "Incant.UnitTest.Core", version.ToString());
        var toolchain = new Installation(
            Kind.Gnu,
            CompilerFamily.Gcc,
            root,
            Platform.OS,
            TargetArchitecture.X64,
            version,
            version,
            channel,
            [source],
            [TargetPlatform.Linux],
            [TargetArchitecture.X64],
            []);
        return new Profile(
            toolchain,
            null,
            TargetPlatform.Linux,
            TargetArchitecture.X64,
            "x86_64-unknown-linux-gnu");
    }

    private static SdkInstallation CreateMacSdk(
        Version version,
        Source source)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "Incant.UnitTest.Core",
            "Xcode",
            version.ToString());
        return new SdkInstallation(
            Kind.Xcode,
            TargetPlatform.MacOS,
            root,
            Path.Combine(root, "SDK"),
            version,
            [source],
            [TargetArchitecture.ARM64]);
    }
}
