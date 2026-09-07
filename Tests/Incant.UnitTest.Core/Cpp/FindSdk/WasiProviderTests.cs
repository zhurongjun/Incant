using Incant.Core.Cpp;
using Incant.Core.Cpp.FindSdk;

namespace Incant.UnitTest.Core.Cpp.FindSdk;

public sealed class WasiProviderTests
{
    [Theory]
    [InlineData(33)]
    [InlineData(34)]
    public async Task TargetHeadersPrecedeGenericHeadersAndExcludeOtherTargets(int version)
    {
        using var fixture = new CompilerFixture();
        string root = fixture.DirectoryPath("wasi");
        fixture.FilePath("wasi/VERSION", $"{version}.0");
        string sysroot = fixture.DirectoryPath("wasi/share/wasi-sysroot");
        foreach (string target in new[] { "wasm32-wasip1", "wasm32-wasip2", "wasm32-wasip1-threads" })
        {
            fixture.FilePath($"wasi/share/wasi-sysroot/include/{target}/stdio.h");
            fixture.FilePath($"wasi/share/wasi-sysroot/include/{target}/c++/v1/array");
            fixture.FilePath($"wasi/share/wasi-sysroot/lib/{target}/libc.a");
        }

        fixture.DirectoryPath("wasi/share/wasi-sysroot/include/c++/v1");
        Sdk sdk = Assert.Single((await FindAsync(root, "wasm32-wasi")).Sdks);
        TargetLayout layout = Assert.Single(sdk.Layouts);
        Assert.Equal("wasm32-wasip1", layout.TargetTriple);
        Assert.Empty(layout.Diagnostics);
        string firstCpp = layout.Resources.First(resource => resource.Purpose == ResourcePurpose.CppInclude).Path;
        Assert.Equal(Path.Combine(sysroot, "include", "wasm32-wasip1", "c++", "v1"), firstCpp);
        Assert.DoesNotContain(layout.Resources, resource =>
            resource.Path.Contains("wasip2", StringComparison.Ordinal)
            || resource.Path.Contains("threads", StringComparison.Ordinal));
        Assert.Empty((await FindAsync(root, "wasm32-wasip2")).Sdks);
    }

    [Fact]
    public async Task OldGenericHeadersRemainUsableWithLegacyTargetLibraries()
    {
        using var fixture = new CompilerFixture();
        string root = fixture.DirectoryPath("wasi");
        fixture.FilePath("wasi/VERSION", "32.0");
        fixture.FilePath("wasi/share/wasi-sysroot/usr/include/stdio.h");
        fixture.FilePath("wasi/share/wasi-sysroot/usr/include/c++/v1/array");
        fixture.FilePath("wasi/share/wasi-sysroot/usr/lib/wasm32-wasi/libc.a");

        Sdk sdk = Assert.Single((await FindAsync(root, "wasm32-wasip1")).Sdks);
        Assert.Empty(Assert.Single(sdk.Layouts).Diagnostics);
    }

    [Fact]
    public async Task EmptyPlaceholdersAndOtherTargetsDoNotCompletePartialInstallation()
    {
        using var fixture = new CompilerFixture();
        string root = fixture.DirectoryPath("wasi");
        fixture.FilePath("wasi/VERSION", "34.0");
        fixture.DirectoryPath("wasi/share/wasi-sysroot/include/c++/v1");
        fixture.FilePath("wasi/share/wasi-sysroot/include/wasm32-wasip2/stdio.h");
        fixture.FilePath("wasi/share/wasi-sysroot/include/wasm32-wasip2/c++/v1/array");

        DiscoveryResult result = await FindAsync(root, "wasm32-wasip1");
        TargetLayout layout = Assert.Single(Assert.Single(result.Sdks).Layouts);
        Assert.Contains(layout.Diagnostics, diagnostic => diagnostic.Message.Contains("stdio.h", StringComparison.Ordinal));
        Assert.Contains(layout.Diagnostics, diagnostic => diagnostic.Message.Contains("array", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("array", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BrokenHeaderLinkPreservesTargetLibrariesAndReportsTheMissingGroup()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "This fixture exercises Unix symbolic link traversal.");
        using var fixture = new CompilerFixture();
        string root = fixture.DirectoryPath("wasi");
        fixture.FilePath("wasi/VERSION", "34.0");
        string include = fixture.DirectoryPath("wasi/share/wasi-sysroot/include");
        Directory.CreateSymbolicLink(Path.Combine(include, "wasm32-wasip1"), "missing");
        string library = fixture.FilePath("wasi/share/wasi-sysroot/lib/wasm32-wasip1/libc.a");

        TargetLayout layout = Assert.Single(Assert.Single((await FindAsync(root, "wasm32-wasip1")).Sdks).Layouts);
        Assert.Contains(layout.Resources, resource => resource.Path == library);
        Assert.Contains(layout.Diagnostics, diagnostic => diagnostic.Message.Contains("stdio.h", StringComparison.Ordinal));
        Assert.Contains(layout.Diagnostics, diagnostic => diagnostic.Message.Contains("missing target", StringComparison.Ordinal));
    }

    private static Task<DiscoveryResult> FindAsync(string root, string triple) =>
        new Finder([new BundleProvider()]).FindSdksAsync(new SdkQuery
        {
            Kind = Kind.WasiSdk,
            RootPath = root,
            TargetTriple = triple,
            Environment = CompilerFixture.Environment(),
        }, TestContext.Current.CancellationToken);
}
