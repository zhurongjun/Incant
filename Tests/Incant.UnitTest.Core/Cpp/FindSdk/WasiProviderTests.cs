using Incant.Core.Cpp;
using Incant.Core.Cpp.FindSdk;

namespace Incant.UnitTest.Core.Cpp.FindSdk;

public sealed class WasiProviderTests
{
    private static readonly string s_sysroot = Path.Combine("wasi", "share", "wasi-sysroot");

    [Theory]
    [InlineData(33)]
    [InlineData(34)]
    public async Task ExceptionVariantsKeepTheirHeadersAndLibrariesTogether(int version)
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, version);
        // The pinned SDK 33/34 archives install C++ headers and runtimes below noeh/eh.
        // https://github.com/WebAssembly/wasi-sdk/blob/wasi-sdk-33/cmake/wasi-sdk-sysroot.cmake
        AddTarget(fixture, "wasm32-wasip1", "noeh", "eh");
        AddTarget(fixture, "wasm32-wasip2", "noeh", "eh");
        AddTarget(fixture, "wasm32-wasip3", "noeh");
        AddTarget(fixture, "wasm32-wasip1-threads", "noeh");
        fixture.DirectoryPath($"{s_sysroot}/include/c++/v1");
        fixture.FilePath($"{s_sysroot}/lib/wasm32-wasip1/noeh/llvm-lto/23.0.0/liblto-only.a");

        Sdk sdk = Assert.Single((await FindAsync(root, "wasm32-wasi")).Sdks);
        Assert.Equal(new[] { ".", "eh" }, sdk.Layouts.Select(layout => layout.Multilib));
        foreach (TargetLayout layout in sdk.Layouts)
        {
            string variant = layout.Multilib == "." ? "noeh" : "eh";
            string otherVariant = variant == "noeh" ? "eh" : "noeh";
            Assert.Equal("wasm32-wasip1", layout.TargetTriple);
            Assert.Empty(layout.Diagnostics);
            Assert.Equal(Path.Combine(fixture.Root, s_sysroot, "include", "wasm32-wasip1", variant, "c++", "v1"),
                layout.Resources.First(resource => resource.Purpose == ResourcePurpose.CppInclude).Path);
            Assert.Equal(Path.Combine(fixture.Root, s_sysroot, "lib", "wasm32-wasip1", variant),
                layout.Resources.First(resource => resource.Purpose == ResourcePurpose.LibraryDirectory).Path);
            Assert.Contains(layout.Resources, resource => resource.Path == Path.Combine(fixture.Root,
                s_sysroot, "lib", "wasm32-wasip1", variant, "libc++.a"));
            Assert.Contains(layout.Resources, resource => resource.Path == Path.Combine(fixture.Root,
                s_sysroot, "lib", "wasm32-wasip1", "crt1-command.o"));
            Assert.DoesNotContain(layout.Resources, resource =>
                resource.Path.Split(Path.DirectorySeparatorChar).Any(component =>
                    component == otherVariant || component == "llvm-lto"
                    || component == "wasm32-wasip2" || component == "wasm32-wasip3"
                    || component == "wasm32-wasip1-threads"));
            Assert.All(layout.Resources, resource => Assert.False(resource.IsExternal));
            Assert.Equal(layout.Resources.Count,
                layout.Resources.Select(resource => (resource.Purpose, resource.Path)).Distinct().Count());
        }

        Assert.Equal(".", (await FindLayoutAsync(root, "wasm32-wasip1", ".")).Multilib);
        Assert.Equal("eh", (await FindLayoutAsync(root, "wasm32-wasi", "eh")).Multilib);
        Assert.Empty((await FindAsync(root, "wasm32-wasip1", "unknown")).Sdks);
        Assert.Empty((await FindAsync(root, "wasm32-wasip2")).Sdks);
        Assert.Empty((await FindAsync(root, "wasm32-wasip3")).Sdks);
        Assert.Empty((await FindAsync(root, "wasm32-wasip1-threads")).Sdks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyTargetAndGenericHeadersKeepAnUnclassifiedLayout(bool generic)
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 32);
        fixture.FilePath($"{s_sysroot}/usr/include/stdio.h");
        string header = generic ? "usr/include/c++/v1/array" : "usr/include/wasm32-wasi/c++/v1/array";
        fixture.FilePath($"{s_sysroot}/{header}");
        foreach (string library in new[] { "libc.a", "libc++.a", "libc++abi.a" })
        {
            fixture.FilePath($"{s_sysroot}/usr/lib/wasm32-wasi/{library}");
        }

        TargetLayout layout = await FindLayoutAsync(root, "wasm32-wasip1");
        Assert.Null(layout.Multilib);
        Assert.Empty(layout.Diagnostics);
        Assert.Empty((await FindAsync(root, "wasm32-wasip1", ".")).Sdks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("noeh")]
    public async Task EmptyPreferredTargetDirectoriesDoNotHideCompleteAliasResources(string variant)
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 34);
        fixture.DirectoryPath($"{s_sysroot}/include/wasm32-wasip1");
        fixture.DirectoryPath($"{s_sysroot}/lib/wasm32-wasip1");
        AddTarget(fixture, "wasm32-wasi", variant);

        TargetLayout layout = await FindLayoutAsync(root, "wasm32-wasip1");
        Assert.Empty(layout.Diagnostics);
        Assert.Contains(layout.Resources, resource => resource.Path == Path.Combine(fixture.Root,
            s_sysroot, "lib", "wasm32-wasi", variant, "libc++.a"));
    }

    [Fact]
    public async Task PartialDefaultVariantCannotBorrowTheExceptionRuntime()
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 34);
        AddTarget(fixture, "wasm32-wasip1", "eh");
        fixture.DirectoryPath($"{s_sysroot}/lib/wasm32-wasip1/noeh");
        // Unclassified C++ resources do not establish the ABI of a named, partial variant.
        AddCppFiles(fixture, "wasm32-wasip1", "");

        Sdk sdk = Assert.Single((await FindAsync(root, "wasm32-wasip1")).Sdks);
        TargetLayout partial = Assert.Single(sdk.Layouts, layout => layout.Multilib == ".");
        Assert.Contains(partial.Diagnostics, diagnostic => diagnostic.Message.Contains("array", StringComparison.Ordinal));
        Assert.Contains(partial.Diagnostics, diagnostic => diagnostic.Message.Contains("libc++.a", StringComparison.Ordinal));
        Assert.DoesNotContain(partial.Resources, resource => Path.GetFileName(resource.Path) == "libc++.a");
        Assert.Empty(Assert.Single(sdk.Layouts, layout => layout.Multilib == "eh").Diagnostics);
        TargetLayout selected = await FindLayoutAsync(root, "wasm32-wasip1", ".");
        Assert.Equal(partial.Resources, selected.Resources);
    }

    [Fact]
    public async Task ExceptionOnlyInstallationDoesNotClaimTheDefaultVariant()
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 34);
        AddTarget(fixture, "wasm32-wasip1", "eh");

        TargetLayout layout = await FindLayoutAsync(root, "wasm32-wasip1");
        Assert.Equal("eh", layout.Multilib);
        Assert.Empty(layout.Diagnostics);
        Assert.Empty((await FindAsync(root, "wasm32-wasip1", ".")).Sdks);
    }

    [Fact]
    public async Task MissingExceptionUnwinderIsSpecificToThatVariant()
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 34);
        AddTarget(fixture, "wasm32-wasip1", "noeh", "eh");
        File.Delete(Path.Combine(fixture.Root, s_sysroot, "lib", "wasm32-wasip1", "eh", "libunwind.a"));

        Sdk sdk = Assert.Single((await FindAsync(root, "wasm32-wasip1")).Sdks);
        Assert.Empty(Assert.Single(sdk.Layouts, layout => layout.Multilib == ".").Diagnostics);
        Assert.Contains(Assert.Single(sdk.Layouts, layout => layout.Multilib == "eh").Diagnostics,
            diagnostic => diagnostic.Message.Contains("libunwind.a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyPlaceholdersAndOtherTargetsDoNotCompletePartialInstallation()
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 34);
        fixture.DirectoryPath($"{s_sysroot}/include/c++/v1");
        AddTarget(fixture, "wasm32-wasip2", "noeh");

        DiscoveryResult result = await FindAsync(root, "wasm32-wasip1");
        TargetLayout layout = Assert.Single(Assert.Single(result.Sdks).Layouts);
        Assert.Null(layout.Multilib);
        Assert.Contains(layout.Diagnostics, diagnostic => diagnostic.Message.Contains("stdio.h", StringComparison.Ordinal));
        Assert.Contains(layout.Diagnostics, diagnostic => diagnostic.Message.Contains("array", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("array", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BrokenHeaderLinkPreservesTargetLibrariesAndReportsTheMissingGroup()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "This fixture exercises Unix symbolic link traversal.");
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 34);
        string include = fixture.DirectoryPath($"{s_sysroot}/include");
        Directory.CreateSymbolicLink(Path.Combine(include, "wasm32-wasip1"), "missing");
        string library = fixture.FilePath($"{s_sysroot}/lib/wasm32-wasip1/libc.a");

        TargetLayout layout = await FindLayoutAsync(root, "wasm32-wasip1");
        Assert.Contains(layout.Resources, resource => resource.Path == library);
        Assert.Contains(layout.Diagnostics, diagnostic => diagnostic.Message.Contains("stdio.h", StringComparison.Ordinal));
        Assert.Contains(layout.Diagnostics, diagnostic => diagnostic.Message.Contains("missing target", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("noeh")]
    public async Task BrokenLibraryLinksDoNotDiscardNeighboringResources(string directory)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "This fixture exercises Unix symbolic link traversal.");
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 34);
        AddTarget(fixture, "wasm32-wasip1", "noeh");
        string libraries = fixture.DirectoryPath($"{s_sysroot}/lib/wasm32-wasip1/{directory}");
        string broken = Path.Combine(libraries, "libbroken.a");
        File.CreateSymbolicLink(broken, "missing.a");
        string survivor = fixture.FilePath($"{s_sysroot}/lib/wasm32-wasip1/{directory}/libsurvivor.a");

        TargetLayout layout = await FindLayoutAsync(root, "wasm32-wasip1", ".");
        Assert.Contains(layout.Resources, resource => resource.Path == survivor);
        Assert.Contains(layout.Resources, resource => Path.GetFileName(resource.Path) == "libc++.a");
        Assert.DoesNotContain(layout.Resources, resource => resource.Path == broken);
        Assert.Contains(layout.Diagnostics, diagnostic => diagnostic.Path == broken);
    }

    [Fact]
    public async Task FreshDiscoveryObservesVariantDamageWithoutChangingExistingSnapshots()
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 34);
        AddTarget(fixture, "wasm32-wasip1", "noeh", "eh");
        TargetLayout before = await FindLayoutAsync(root, "wasm32-wasip1", ".");
        string library = Path.Combine(fixture.Root, s_sysroot, "lib", "wasm32-wasip1", "noeh", "libc++.a");
        File.Delete(library);

        TargetLayout after = await FindLayoutAsync(root, "wasm32-wasip1", ".");
        Assert.Contains(before.Resources, resource => resource.Path == library);
        Assert.Empty(before.Diagnostics);
        Assert.DoesNotContain(after.Resources, resource => resource.Path == library);
        Assert.Contains(after.Diagnostics, diagnostic => diagnostic.Message.Contains("libc++.a", StringComparison.Ordinal));
    }

    private static string Installation(CompilerFixture fixture, int version)
    {
        string root = fixture.DirectoryPath("wasi");
        fixture.FilePath("wasi/VERSION", $"{version}.0");
        fixture.DirectoryPath(s_sysroot);
        return root;
    }

    private static void AddTarget(CompilerFixture fixture, string target, params string[] variants)
    {
        fixture.FilePath($"{s_sysroot}/include/{target}/stdio.h");
        fixture.FilePath($"{s_sysroot}/lib/{target}/libc.a");
        fixture.FilePath($"{s_sysroot}/lib/{target}/crt1-command.o");
        foreach (string variant in variants)
        {
            AddCppFiles(fixture, target, variant);
        }
    }

    private static void AddCppFiles(CompilerFixture fixture, string target, string variant)
    {
        fixture.FilePath($"{s_sysroot}/include/{target}/{variant}/c++/v1/array");
        foreach (string library in new[] { "libc++.a", "libc++abi.a" })
        {
            fixture.FilePath($"{s_sysroot}/lib/{target}/{variant}/{library}");
        }

        if (variant == "eh")
        {
            fixture.FilePath($"{s_sysroot}/lib/{target}/{variant}/libunwind.a");
        }
    }

    private static async Task<TargetLayout> FindLayoutAsync(string root, string triple, string? multilib = null)
    {
        DiscoveryResult result = await FindAsync(root, triple, multilib);
        return Assert.Single(Assert.Single(result.Sdks).Layouts);
    }

    private static Task<DiscoveryResult> FindAsync(string root, string triple, string? multilib = null) =>
        new Finder([new BundleProvider()]).FindSdksAsync(new SdkQuery
        {
            Kind = Kind.WasiSdk,
            RootPath = root,
            TargetTriple = triple,
            Multilib = multilib,
            Environment = CompilerFixture.Environment(),
        }, TestContext.Current.CancellationToken);
}
