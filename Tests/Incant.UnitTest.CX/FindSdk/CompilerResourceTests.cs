using Incant.CX;
using Incant.CX.FindSdk;
using Incant.TestSupport;

namespace Incant.UnitTest.CX.FindSdk;

public sealed class CompilerResourceTests
{
    [Fact]
    public async Task DefaultAndExplicitSysrootsKeepTheirQuerySemantics()
    {
        using var fixture = new CompilerFixture();
        string include = fixture.DirectoryPath("include");
        fixture.FilePath("include/array");
        string compiler = fixture.Compiler("bin/clang-18", new Dictionary<string, object>
        {
            ["CppIncludes"] = new[] { include },
            ["CIncludes"] = new[] { include },
            ["Resource"] = fixture.DirectoryPath("resources"),
        });

        TargetLayout initial = Assert.Single((await FindAsync(compiler)).Layouts);
        Assert.Null(initial.SysrootPath);
        IReadOnlyList<CompilerInvocation> initialInvocations = CompilerFixture.Invocations(compiler);
        Assert.DoesNotContain(initialInvocations.SelectMany(invocation => invocation.Arguments),
            argument => argument.StartsWith("--sysroot=", StringComparison.Ordinal));

        string root = Path.GetPathRoot(fixture.Root)!;
        TargetLayout explicitRoot = Assert.Single((await FindAsync(compiler, root, initial.TargetTriple)).Layouts);
        Assert.Equal(root, explicitRoot.SysrootPath);
        CompilerInvocation[] targetedInvocations = CompilerFixture.Invocations(compiler)
            .ExceptBy(initialInvocations.Select(invocation => invocation.Id), invocation => invocation.Id).ToArray();
        Assert.Contains("--sysroot=" + root, targetedInvocations.SelectMany(invocation => invocation.Arguments));
        Assert.Contains("--target=" + initial.TargetTriple, targetedInvocations.SelectMany(invocation => invocation.Arguments));
        Assert.All(initialInvocations.Concat(targetedInvocations), invocation =>
        {
            Assert.NotNull(invocation.CompletedTimestamp);
            Assert.Equal(0, invocation.ExitCode);
        });
        Assert.Equal(targetedInvocations.Length, targetedInvocations.Select(invocation => invocation.Id).Distinct().Count());
        Assert.Equal(initial.Resources.Select(resource => resource.Path), explicitRoot.Resources.Select(resource => resource.Path));
    }

    [Fact]
    public async Task SymlinkParentsResolveBeforeCheckingHeaderLibraryAndObjectPaths()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix parent traversal follows the resolved symlink directory.");
        using var fixture = new CompilerFixture();
        fixture.DirectoryPath("usr/lib");
        fixture.DirectoryPath("usr/lib/gcc/x86_64-linux-gnu/14");
        string cpp = fixture.DirectoryPath("usr/include/c++/14");
        fixture.FilePath("usr/include/c++/14/array");
        string library = fixture.DirectoryPath("usr/lib64");
        string startup = fixture.FilePath("usr/lib64/crtbegin.o");
        var elf = new byte[64];
        elf[0] = 0x7f;
        elf[1] = (byte)'E';
        elf[2] = (byte)'L';
        elf[3] = (byte)'F';
        elf[4] = 2;
        elf[5] = 1;
        elf[18] = 62;
        File.WriteAllBytes(startup, elf);
        string link = Path.Combine(fixture.Root, "lib");
        Directory.CreateSymbolicLink(link, "usr/lib");
        string secondLink = Path.Combine(fixture.Root, "lib-alias");
        Directory.CreateSymbolicLink(secondLink, "lib");
        string reportedCpp = Path.Combine(secondLink, "gcc", "x86_64-linux-gnu", "14",
            "..", "..", "..", "..", "include", "c++", "14");
        string reportedLibrary = Path.Combine(secondLink, "..", "lib64");
        string compiler = fixture.Compiler("bin/clang-18", new Dictionary<string, object>
        {
            ["CppIncludes"] = new[] { reportedCpp, cpp },
            ["CIncludes"] = new[] { cpp },
            ["Libraries"] = new[] { reportedLibrary },
            ["Resource"] = fixture.DirectoryPath("resources"),
            ["crtbegin.o"] = Path.Combine(reportedLibrary, "crtbegin.o"),
        });

        TargetLayout layout = Assert.Single((await FindAsync(compiler)).Layouts);
        Assert.Single(layout.Resources, resource => resource.Purpose == ResourcePurpose.CppInclude && resource.Path == cpp);
        Assert.Contains(layout.Resources, resource => resource.Purpose == ResourcePurpose.LibraryDirectory && resource.Path == library);
        Assert.Contains(layout.Resources, resource => resource.Purpose == ResourcePurpose.Startup && resource.Path == startup);
        Assert.DoesNotContain(layout.Diagnostics, diagnostic => diagnostic.Message.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BrokenAndCyclicHeaderLinksAreDiagnosedWithoutDiscardingValidResources()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "This fixture exercises Unix symbolic link traversal.");
        using var fixture = new CompilerFixture();
        string include = fixture.DirectoryPath("include");
        string broken = Path.Combine(fixture.Root, "broken");
        string cycle = Path.Combine(fixture.Root, "cycle");
        Directory.CreateSymbolicLink(broken, "missing");
        Directory.CreateSymbolicLink(cycle, "cycle");
        string compiler = fixture.Compiler("bin/clang-18", new Dictionary<string, object>
        {
            ["CppIncludes"] = new[] { broken, cycle, include },
            ["CIncludes"] = new[] { include },
            ["Resource"] = fixture.DirectoryPath("resources"),
        });

        Sdk sdk = await FindAsync(compiler);
        TargetLayout layout = Assert.Single(sdk.Layouts);
        Assert.Contains(layout.Resources, resource => resource.Path == include);
        Assert.Contains(layout.Diagnostics, diagnostic => diagnostic.Path == broken);
        Assert.Contains(layout.Diagnostics, diagnostic => diagnostic.Path == cycle);
    }

    [Fact]
    public async Task RealCompilerAliasesMergeAndTheExplicitInvocationIsRetained()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic link creation is not guaranteed for Windows test users.");
        using var fixture = new CompilerFixture();
        string compiler = fixture.Compiler("bin/clang-18");
        string alias = Path.Combine(Path.GetDirectoryName(compiler)!, "clang");
        File.CreateSymbolicLink(alias, "clang-18");
        var finder = new Incant.CX.FindTools.Finder([new Incant.CX.FindTools.CompilerProvider()]);
        Incant.CX.FindTools.DiscoveryResult result = await finder.FindToolSetsAsync(
            new Incant.CX.FindTools.ToolSetQuery
            {
                RootPath = Path.GetDirectoryName(compiler),
                Environment = CompilerFixture.Environment(),
            }, TestContext.Current.CancellationToken);
        Assert.Single(result.ToolSets);
        Sdk sdk = await FindAsync(alias);
        Assert.Equal(alias, sdk.CompilerPath);
    }

    [Fact]
    public async Task FailedHeaderProbeReportsTheOperationAndPreservesOtherResources()
    {
        using var fixture = new CompilerFixture();
        string resources = fixture.DirectoryPath("resources");
        string compiler = fixture.Compiler("bin/clang-18", new Dictionary<string, object>
        {
            ["Resource"] = resources,
            ["FailArgument"] = "-v",
            ["ProbeError"] = "controlled header probe failure",
            ["ProbeExitCode"] = 23,
        });

        TargetLayout layout = Assert.Single((await FindAsync(compiler)).Layouts);
        Assert.Contains(layout.Resources, resource => resource.Path == resources);
        Assert.Contains(layout.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("exit=23", StringComparison.Ordinal)
            && diagnostic.Message.Contains("controlled header probe failure", StringComparison.Ordinal)
            && diagnostic.Message.Contains("-v", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AncestorLinksDoNotChangeResourceIdentityOrTheExplicitCompilerEntry()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "This fixture requires Unix symbolic links.");
        using var parent = new CompilerFixture();
        string physical = parent.DirectoryPath("physical");
        string alias = Path.Combine(parent.Root, "alias");
        Directory.CreateSymbolicLink(alias, "physical");
        using var fixture = new CompilerFixture(alias);
        Assert.StartsWith(physical + Path.DirectorySeparatorChar, fixture.Root);
        string include = fixture.DirectoryPath("include");
        string reportedInclude = Path.Combine(alias, Path.GetRelativePath(physical, include));
        string compiler = fixture.Compiler("bin/clang-18", new Dictionary<string, object>
        {
            ["CppIncludes"] = new[] { reportedInclude, include },
            ["CIncludes"] = new[] { reportedInclude },
            ["Resource"] = fixture.DirectoryPath("resources"),
        });
        string compilerEntry = Path.Combine(alias, Path.GetRelativePath(physical, compiler));

        Sdk sdk = await FindAsync(compilerEntry);
        Assert.Equal(compilerEntry, sdk.CompilerPath);
        Assert.Single(Assert.Single(sdk.Layouts).Resources,
            resource => resource.Purpose == ResourcePurpose.CppInclude && resource.Path == include);
    }

    private static async Task<Sdk> FindAsync(string compiler, string? sysroot = null, string? triple = null)
    {
        DiscoveryResult result = await new Finder([new CompilerProvider()]).FindSdksAsync(new SdkQuery
        {
            Kind = Kind.Llvm,
            CompilerPath = compiler,
            SysrootPath = sysroot,
            TargetTriple = triple,
            Environment = CompilerFixture.Environment(),
        }, TestContext.Current.CancellationToken);
        return Assert.Single(result.Sdks);
    }
}
