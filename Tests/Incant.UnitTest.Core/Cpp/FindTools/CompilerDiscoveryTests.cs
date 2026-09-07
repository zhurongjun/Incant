using System.Text;
using Incant.Core.Cpp;
using Incant.Core.Cpp.FindTools;

namespace Incant.UnitTest.Core.Cpp.FindTools;

public sealed class CompilerDiscoveryTests
{
    [Fact]
    public async Task DirectoryDiscoveryRejectsUnconfirmedPrefixButExplicitWrapperIsPreserved()
    {
        using var fixture = new CompilerFixture();
        var identity = new Dictionary<string, object>
        {
            ["Identity"] = "gcc (GCC) 13.3.0 Free Software Foundation",
            ["Version"] = "13.3.0",
        };
        string compiler = fixture.Compiler("bin/gcc-13", identity);
        string wrapper = fixture.Compiler("bin/c89-gcc", identity);
        DiscoveryResult result = await FindAsync(Path.GetDirectoryName(compiler)!);
        Assert.Equal(compiler, Assert.Single(result.ToolSets).CompilerPath);
        ToolSet explicitWrapper = Assert.Single((await FindAsync(wrapper)).ToolSets);
        Assert.Equal(wrapper, explicitWrapper.CompilerPath);
        Assert.Equal(wrapper, (await explicitWrapper.FindToolAsync(
            ToolNames.Gcc, cancellationToken: TestContext.Current.CancellationToken))?.Path);
    }

    [Fact]
    public async Task SameVersionIndependentEntriesAreNotMergedThroughOneCompanion()
    {
        using var fixture = new CompilerFixture();
        string first = fixture.Compiler("bin/clang-18");
        string second = fixture.Compiler("bin/clang18");
        fixture.Compiler("bin/clang++-18");

        DiscoveryResult result = await FindAsync(Path.GetDirectoryName(first)!);
        Assert.Equal(2, result.ToolSets.Count);
        Assert.Contains(result.ToolSets, toolSet => toolSet.CompilerPath == first);
        Assert.Contains(result.ToolSets, toolSet => toolSet.CompilerPath == second);
    }

    [Fact]
    public async Task TargetPrefixedDriversAreAcceptedOnlyForTheirReportedTarget()
    {
        using var fixture = new CompilerFixture();
        string compiler = fixture.Compiler("bin/x86_64-linux-gnu-clang-18");
        fixture.Compiler("bin/aarch64-linux-gnu-clang-18");

        DiscoveryResult result = await FindAsync(Path.GetDirectoryName(compiler)!);
        Assert.Equal(compiler, Assert.Single(result.ToolSets).CompilerPath);
    }

    [Fact]
    public async Task MismatchedCompanionVersionCannotSupplyTheCppDriver()
    {
        using var fixture = new CompilerFixture();
        string compiler = fixture.Compiler("bin/clang-18");
        fixture.Compiler("bin/clang++-18", new Dictionary<string, object> { ["Identity"] = "clang version 19.0.0" });
        ToolSet toolSet = Assert.Single((await FindAsync(compiler)).ToolSets);
        Assert.Null(await toolSet.FindToolAsync(ToolNames.Clangxx,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NonzeroIdentityExitIsReportedWithoutRetryAndDoesNotHideHealthyCandidate()
    {
        using var fixture = new CompilerFixture();
        string healthy = fixture.Compiler("bin/clang-18");
        string broken = fixture.Compiler("bin/clang-19", new Dictionary<string, object>
        {
            ["ExitCode"] = 17,
            ["Error"] = new string('界', 5000) + "last-error",
        });

        DiscoveryResult result = await FindAsync(Path.GetDirectoryName(healthy)!);
        Assert.Equal(healthy, Assert.Single(result.ToolSets).CompilerPath);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics, item => item.Path == broken);
        Assert.Contains("NonzeroExit", diagnostic.Message);
        Assert.Contains("exit=17", diagnostic.Message);
        Assert.Contains("last-error", diagnostic.Message);
        Assert.True(Encoding.UTF8.GetByteCount(diagnostic.Message) < 6000);
        Assert.Single(File.ReadAllLines(broken + ".compiler.json.arguments"));
    }

    [Fact]
    public async Task TimedOutIdentityIsRetriedOnceAndRecoveryRetainsEvidence()
    {
        using var fixture = new CompilerFixture();
        string compiler = fixture.Compiler("bin/clang-18", new Dictionary<string, object>
        {
            ["DelayMilliseconds"] = 30000,
            ["DelayOnce"] = 1,
        });

        DiscoveryResult result = await FindAsync(compiler, TimeSpan.FromSeconds(2));
        Assert.Equal(compiler, Assert.Single(result.ToolSets).CompilerPath);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("TimedOut", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("recovered", StringComparison.Ordinal));
        Assert.Equal(2, File.ReadAllLines(compiler + ".compiler.json.arguments")
            .Count(line => line.Contains("--version", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FailedIdentityDetailRemainsVisibleWithoutDiscardingRecognizedCompiler()
    {
        using var fixture = new CompilerFixture();
        string compiler = fixture.Compiler("bin/clang-18", new Dictionary<string, object>
        {
            ["FailArgument"] = "-dumpmachine",
            ["ProbeError"] = "controlled target query failure",
            ["ProbeExitCode"] = 29,
        });

        DiscoveryResult result = await FindAsync(compiler);
        Assert.Equal(compiler, Assert.Single(result.ToolSets).CompilerPath);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("-dumpmachine", StringComparison.Ordinal)
            && diagnostic.Message.Contains("exit=29", StringComparison.Ordinal)
            && diagnostic.Message.Contains("controlled target query failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownIdentityAndStartFailureHaveDifferentDiagnostics()
    {
        using var fixture = new CompilerFixture();
        string unknown = fixture.Compiler("bin/clang-18", new Dictionary<string, object> { ["Identity"] = "unrelated tool" });
        DiscoveryException identityError = await Assert.ThrowsAsync<DiscoveryException>(() => FindAsync(unknown));
        Assert.Contains(identityError.Diagnostics, diagnostic => diagnostic.Message.Contains("UnrecognizedIdentity", StringComparison.Ordinal));

        string broken = fixture.FilePath("broken/clang" + (OperatingSystem.IsWindows() ? ".exe" : ""), "not an executable");
        DiscoveryException startError = await Assert.ThrowsAsync<DiscoveryException>(() => FindAsync(broken));
        Assert.Contains(startError.Diagnostics, diagnostic => diagnostic.Message.Contains("StartFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationDuringIdentityProbePropagates()
    {
        using var fixture = new CompilerFixture();
        string compiler = fixture.Compiler("bin/clang-18", new Dictionary<string, object> { ["DelayMilliseconds"] = 30000 });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<DiscoveryResult> pending = FindAsync(compiler, TimeSpan.FromSeconds(30), cancellation.Token);
        try
        {
            await WaitUntilAsync(() => File.Exists(compiler + ".compiler.json.arguments"));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
                // The canceled discovery has reaped its child before fixture cleanup.
            }
        }
    }

    [Fact]
    public async Task IdentityBatchRunsAtMostFourCandidatesAndCompletesAllCandidates()
    {
        using var fixture = new CompilerFixture();
        string release = Path.Combine(fixture.Root, "release");
        string[] compilers = Enumerable.Range(11, 6).Select(version =>
            fixture.Compiler($"bin/clang-{version}", new Dictionary<string, object>
            {
                ["Identity"] = $"clang version {version}.0.0",
                ["ReleaseFile"] = release,
            })).ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<DiscoveryResult> pending = FindAsync(Path.GetDirectoryName(compilers[0])!,
            TimeSpan.FromSeconds(30), cancellation.Token);
        try
        {
            await WaitUntilAsync(() => compilers.Count(path => File.Exists(path + ".compiler.json.arguments")) >= 4);
            Assert.Equal(4, compilers.Count(path => File.Exists(path + ".compiler.json.arguments")));
            File.WriteAllText(release, "");
            Assert.Equal(6, (await pending).ToolSets.Count);
        }
        finally
        {
            File.WriteAllText(release, "");
            cancellation.Cancel();
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
                // Ensure no helper remains active when the fixture is removed.
            }
        }
    }

    private static Task<DiscoveryResult> FindAsync(string root, TimeSpan? timeout = null, CancellationToken? token = null) =>
        new Finder([new CompilerProvider()]).FindToolSetsAsync(new ToolSetQuery
        {
            RootPath = root,
            IncludePreview = true,
            ProbeTimeout = timeout ?? TimeSpan.FromSeconds(10),
            Environment = CompilerFixture.Environment(),
        }, token ?? TestContext.Current.CancellationToken);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }
}
