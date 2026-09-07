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

    [Theory]
    [InlineData("wasm32-wasip1", "")]
    [InlineData("wasm32-wasi", "")]
    [InlineData("wasm32-wasip1", "noeh")]
    [InlineData("wasm32-wasi", "noeh")]
    [InlineData("wasm32-wasip1", "eh")]
    [InlineData("wasm32-wasi", "eh")]
    public async Task CompleteTargetAliasesSelectOneResourceGroup(string queryTarget, string variant)
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 33);
        // SDK 33 contains independent copies, so filesystem identity cannot deduplicate these aliases.
        AddTarget(fixture, "wasm32-wasip1", variant);
        AddTarget(fixture, "wasm32-wasi", variant);

        TargetLayout layout = await FindLayoutAsync(root, queryTarget);
        Assert.Empty(layout.Diagnostics);
        AssertSelectedGroup(fixture, layout, "wasm32-wasip1");
        Assert.DoesNotContain(layout.Resources, resource =>
            resource.Path.Split(Path.DirectorySeparatorChar).Contains("wasm32-wasi"));
    }

    [Theory]
    [InlineData("include/wasm32-wasip1/stdio.h")]
    [InlineData("include/wasm32-wasip1/noeh/c++/v1/array")]
    [InlineData("lib/wasm32-wasip1/libc.a")]
    [InlineData("lib/wasm32-wasip1/noeh/libc++.a")]
    [InlineData("lib/wasm32-wasip1/noeh/libc++abi.a")]
    public async Task CompleteAliasReplacesTheWholePartialPreferredGroup(string missingFile)
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 33);
        AddTarget(fixture, "wasm32-wasip1", "noeh");
        AddTarget(fixture, "wasm32-wasi", "noeh");
        File.Delete(Path.Combine(fixture.Root, s_sysroot, missingFile));

        TargetLayout layout = await FindLayoutAsync(root, "wasm32-wasip1", ".");
        Assert.Empty(layout.Diagnostics);
        AssertSelectedGroup(fixture, layout, "wasm32-wasi");
        Assert.DoesNotContain(layout.Resources, resource =>
            resource.Path.Split(Path.DirectorySeparatorChar).Contains("wasm32-wasip1"));
    }

    [Fact]
    public async Task PartialAliasesCannotCompleteEachOther()
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 33);
        AddTarget(fixture, "wasm32-wasip1", "noeh");
        AddTarget(fixture, "wasm32-wasi", "noeh");
        File.Delete(Path.Combine(fixture.Root, s_sysroot, "lib", "wasm32-wasip1", "noeh", "libc++.a"));
        File.Delete(Path.Combine(fixture.Root, s_sysroot, "include", "wasm32-wasi", "noeh", "c++", "v1", "array"));

        TargetLayout layout = await FindLayoutAsync(root, "wasm32-wasip1", ".");
        Assert.Contains(layout.Resources, resource => resource.Path == Path.Combine(fixture.Root,
            s_sysroot, "include", "wasm32-wasip1", "noeh", "c++", "v1"));
        Assert.Contains(layout.Diagnostics, diagnostic => diagnostic.Message.Contains("libc++.a", StringComparison.Ordinal));
        Assert.DoesNotContain(layout.Resources, resource => Path.GetFileName(resource.Path) == "libc++.a");
        Assert.DoesNotContain(layout.Resources, resource =>
            resource.Path.Split(Path.DirectorySeparatorChar).Contains("wasm32-wasi"));
    }

    [Fact]
    public async Task EachExceptionVariantSelectsItsOwnCompleteGroup()
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 33);
        AddTarget(fixture, "wasm32-wasip1", "noeh", "eh");
        AddTarget(fixture, "wasm32-wasi", "noeh", "eh");
        File.Delete(Path.Combine(fixture.Root, s_sysroot, "lib", "wasm32-wasip1", "eh", "libunwind.a"));
        File.Delete(Path.Combine(fixture.Root, s_sysroot, "lib", "wasm32-wasi", "noeh", "libc++.a"));

        Sdk sdk = Assert.Single((await FindAsync(root, "wasm32-wasip1")).Sdks);
        Assert.Equal(new[] { ".", "eh" }, sdk.Layouts.Select(layout => layout.Multilib));
        Assert.All(sdk.Layouts, layout => Assert.Empty(layout.Diagnostics));
        AssertSelectedGroup(fixture, sdk.Layouts[0], "wasm32-wasip1");
        AssertSelectedGroup(fixture, sdk.Layouts[1], "wasm32-wasi");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SysrootPrefixesAreAlternativeResourceGroups(bool primaryIncomplete)
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 34);
        AddTarget(fixture, "wasm32-wasip1", "noeh");
        AddTargetAt(fixture, "usr", "wasm32-wasip1", "noeh");
        if (primaryIncomplete)
        {
            File.Delete(Path.Combine(fixture.Root, s_sysroot, "include", "wasm32-wasip1", "noeh", "c++", "v1", "array"));
        }

        TargetLayout layout = await FindLayoutAsync(root, "wasm32-wasip1", ".");
        Assert.Empty(layout.Diagnostics);
        AssertSelectedGroup(fixture, layout, "wasm32-wasip1", primaryIncomplete ? "usr" : "");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparatedIncludeAndLibraryPrefixesRemainSupported(bool headersUnderUsr)
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 34);
        string includePrefix = headersUnderUsr ? "usr" : "";
        string libraryPrefix = headersUnderUsr ? "" : "usr";
        fixture.FilePath($"{s_sysroot}/{includePrefix}/include/wasm32-wasip1/stdio.h");
        fixture.FilePath($"{s_sysroot}/{includePrefix}/include/wasm32-wasip1/noeh/c++/v1/array");
        fixture.FilePath($"{s_sysroot}/{libraryPrefix}/lib/wasm32-wasip1/libc.a");
        fixture.FilePath($"{s_sysroot}/{libraryPrefix}/lib/wasm32-wasip1/noeh/libc++.a");
        fixture.FilePath($"{s_sysroot}/{libraryPrefix}/lib/wasm32-wasip1/noeh/libc++abi.a");

        TargetLayout layout = await FindLayoutAsync(root, "wasm32-wasip1", ".");
        Assert.Empty(layout.Diagnostics);
        AssertSelectedGroup(fixture, layout, "wasm32-wasip1", includePrefix, libraryPrefix);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyCppHeaderLocationsAreAlternatives(bool targetHeadersIncomplete)
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 32);
        AddTarget(fixture, "wasm32-wasi", "");
        fixture.FilePath($"{s_sysroot}/include/c++/v1/array");
        if (targetHeadersIncomplete)
        {
            File.Delete(Path.Combine(fixture.Root, s_sysroot, "include", "wasm32-wasi", "c++", "v1", "array"));
            fixture.FilePath($"{s_sysroot}/include/wasm32-wasi/c++/v1/math.h");
        }

        TargetLayout layout = await FindLayoutAsync(root, "wasm32-wasip1");
        Resource headers = Assert.Single(layout.Resources, resource => resource.Purpose == ResourcePurpose.CppInclude
            && resource.Path.EndsWith(Path.Combine("c++", "v1"), StringComparison.Ordinal));
        string expected = targetHeadersIncomplete ? "include/c++/v1" : "include/wasm32-wasi/c++/v1";
        Assert.Equal(Path.GetFullPath(Path.Combine(fixture.Root, s_sysroot, expected)), headers.Path);
        Assert.Empty(layout.Diagnostics);
    }

    [Fact]
    public async Task EmptyPreferredVariantDoesNotHidePartialAliasResources()
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 33);
        fixture.DirectoryPath($"{s_sysroot}/include/wasm32-wasip1/noeh/c++/v1");
        fixture.DirectoryPath($"{s_sysroot}/lib/wasm32-wasip1/noeh");
        AddTarget(fixture, "wasm32-wasi", "noeh");
        File.Delete(Path.Combine(fixture.Root, s_sysroot, "lib", "wasm32-wasi", "noeh", "libc++.a"));

        TargetLayout layout = await FindLayoutAsync(root, "wasm32-wasip1", ".");
        Assert.Contains(layout.Resources, resource => resource.Path == Path.Combine(fixture.Root,
            s_sysroot, "include", "wasm32-wasi", "noeh", "c++", "v1"));
        Assert.DoesNotContain(layout.Resources, resource =>
            resource.Path.Split(Path.DirectorySeparatorChar).Contains("wasm32-wasip1"));
        Assert.Contains(layout.Diagnostics, diagnostic => diagnostic.Message.Contains("libc++.a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FreshDiscoveryReevaluatesAliasPreferenceWithoutChangingSnapshots()
    {
        using var fixture = new CompilerFixture();
        string root = Installation(fixture, 33);
        AddTarget(fixture, "wasm32-wasip1", "noeh");
        AddTarget(fixture, "wasm32-wasi", "noeh");
        TargetLayout before = await FindLayoutAsync(root, "wasm32-wasip1", ".");
        string library = Path.Combine(fixture.Root, s_sysroot, "lib", "wasm32-wasip1", "noeh", "libc++.a");
        File.Delete(library);

        TargetLayout damaged = await FindLayoutAsync(root, "wasm32-wasip1", ".");
        fixture.FilePath($"{s_sysroot}/lib/wasm32-wasip1/noeh/libc++.a");
        TargetLayout repaired = await FindLayoutAsync(root, "wasm32-wasip1", ".");

        AssertSelectedGroup(fixture, before, "wasm32-wasip1");
        AssertSelectedGroup(fixture, damaged, "wasm32-wasi");
        AssertSelectedGroup(fixture, repaired, "wasm32-wasip1");
        Assert.Empty(before.Diagnostics);
        Assert.Empty(damaged.Diagnostics);
        Assert.Empty(repaired.Diagnostics);
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

    private static void AddTarget(CompilerFixture fixture, string target, params string[] variants) =>
        AddTargetAt(fixture, "", target, variants);

    private static void AddTargetAt(CompilerFixture fixture, string prefix, string target, params string[] variants)
    {
        fixture.FilePath($"{s_sysroot}/{prefix}/include/{target}/stdio.h");
        fixture.FilePath($"{s_sysroot}/{prefix}/lib/{target}/libc.a");
        fixture.FilePath($"{s_sysroot}/{prefix}/lib/{target}/crt1-command.o");
        foreach (string variant in variants)
        {
            AddCppFiles(fixture, target, variant, prefix);
        }
    }

    private static void AddCppFiles(CompilerFixture fixture, string target, string variant, string prefix = "")
    {
        fixture.FilePath($"{s_sysroot}/{prefix}/include/{target}/{variant}/c++/v1/array");
        foreach (string library in new[] { "libc++.a", "libc++abi.a" })
        {
            fixture.FilePath($"{s_sysroot}/{prefix}/lib/{target}/{variant}/{library}");
        }

        if (variant == "eh")
        {
            fixture.FilePath($"{s_sysroot}/{prefix}/lib/{target}/{variant}/libunwind.a");
        }
    }

    private static void AssertSelectedGroup(
        CompilerFixture fixture,
        TargetLayout layout,
        string target,
        string prefix = "",
        string? libraryPrefix = null)
    {
        string variant = layout.Multilib switch
        {
            "." => "noeh",
            "eh" => "eh",
            _ => "",
        };
        string root = Path.Combine(fixture.Root, s_sysroot, prefix);
        string include = Path.Combine(root, "include");
        string library = Path.Combine(fixture.Root, s_sysroot, libraryPrefix ?? prefix, "lib", target);
        Assert.Equal("wasm32-wasip1", layout.TargetTriple);
        Assert.Equal(new[] { Path.Combine(include, target, variant, "c++", "v1"), Path.Combine(include, target), include },
            layout.Resources.Where(resource => resource.Purpose == ResourcePurpose.CppInclude).Select(resource => resource.Path));
        Assert.Equal(new[] { Path.Combine(include, target), include },
            layout.Resources.Where(resource => resource.Purpose == ResourcePurpose.CInclude).Select(resource => resource.Path));
        Assert.Equal(new[] { Path.Combine(library, variant), library }.Distinct(),
            layout.Resources.Where(resource => resource.Purpose == ResourcePurpose.LibraryDirectory).Select(resource => resource.Path));
        Assert.Contains(layout.Resources, resource => resource.Path == Path.Combine(library, "libc.a"));
        Assert.Contains(layout.Resources, resource => resource.Path == Path.Combine(library, variant, "libc++.a"));
        Assert.Contains(layout.Resources, resource => resource.Path == Path.Combine(library, variant, "libc++abi.a"));
        if (variant == "eh")
        {
            Assert.Contains(layout.Resources, resource => resource.Path == Path.Combine(library, variant, "libunwind.a"));
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
