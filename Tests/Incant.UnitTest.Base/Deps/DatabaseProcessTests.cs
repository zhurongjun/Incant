using System.Diagnostics;
using Incant.Base.Deps;

namespace Incant.UnitTest.Base.Deps;

public sealed class DatabaseProcessTests
{
    [Fact]
    public async Task ClosingAndReopeningTransfersWriterOwnershipAcrossProcesses()
    {
        using var directory = new DatabaseTestDirectory();
        Database database = directory.Create();
        using Process child = StartWriter(directory);
        try
        {
            await ExpectAsync(child, "ready");
            Assert.False(database.TryOpen());
            Assert.False(database.IsOpened);
            await SendAsync(child, "close");
            await ExpectAsync(child, "closed");
            Assert.True(database.TryOpen());
            Assert.False(database.IsOutdated("stable", null, ["new"]));
            database.Close();

            await SendAsync(child, "open");
            await ExpectAsync(child, "opened");
            Assert.False(database.TryOpen());
            await SendAsync(child, "append");
            await ExpectAsync(child, "appended");
            await SendAsync(child, "close");
            await ExpectAsync(child, "closed");
            using DatabaseScope scope = Database.OpenScoped(database);
            Assert.False(database.IsOutdated("later", null, ["after"]));
        }
        finally
        {
            database.Close();
            await KillAsync(child);
        }
    }

    [Fact]
    public async Task CrossProcessWriterExclusionAndReadOnlySnapshotsSurviveWriterDeath()
    {
        using var directory = new DatabaseTestDirectory();
        using Process child = StartWriter(directory);
        try
        {
            await ExpectAsync(child, "ready");
            Assert.ThrowsAny<IOException>(() => directory.Create().Open());
            Database original = directory.Create(readOnly: true);
            using DatabaseScope originalScope = Database.OpenScoped(original);
            Assert.False(original.IsOutdated("stable", null, ["new"]));
            await SendAsync(child, "append");
            await ExpectAsync(child, "appended");
            Assert.True(original.IsOutdated("later", null, ["after"]));
            Database current = directory.Create(readOnly: true);
            using (Database.OpenScoped(current))
            {
                Assert.False(current.IsOutdated("later", null, ["after"]));
            }

            await KillAsync(child);
            Database recovered = directory.Create();
            using DatabaseScope recoveredScope = Database.OpenScoped(recovered);
            Assert.False(recovered.IsOutdated("stable", null, ["new"]));
            Assert.False(recovered.IsOutdated("later", null, ["after"]));
        }
        finally
        {
            await KillAsync(child);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KillingBeforeOrAfterCompactionLeavesACompleteDatabase(bool afterCompaction)
    {
        using var directory = new DatabaseTestDirectory();
        using Process child = StartWriter(directory);
        try
        {
            await ExpectAsync(child, "ready");
            await SendAsync(child, afterCompaction ? "compact" : "wait-before-compact");
            await ExpectAsync(child, afterCompaction ? "compacted" : "before-compact");

            await KillAsync(child);
            Database recovered = directory.Create();
            using DatabaseScope recoveredScope = Database.OpenScoped(recovered);
            Assert.Equal(2, recovered.Count);
            Assert.False(recovered.IsOutdated("stable", null, ["new"]));
            Assert.False(recovered.IsOutdated("other", null, ["kept"]));
            recovered.Compact();
            Assert.Equal(2, recovered.TotalRecordCount);
            Assert.False(File.Exists(directory.DatabasePath + ".compact"));
        }
        finally
        {
            await KillAsync(child);
        }
    }

    private static Process StartWriter(DatabaseTestDirectory directory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        string[] arguments =
        [
            "exec",
            "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Incant.UnitTest.Base.runtimeconfig.json"),
            "--depsfile", Path.Combine(AppContext.BaseDirectory, "Incant.UnitTest.Base.deps.json"),
            Path.Combine(AppContext.BaseDirectory, "Incant.ProcessTestHelper.dll"),
            "deps", directory.DatabasePath
        ];
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("The database test process did not start.");
    }

    private static async Task SendAsync(Process child, string command)
    {
        await child.StandardInput.WriteLineAsync(command.AsMemory(), TestContext.Current.CancellationToken);
        await child.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ExpectAsync(Process child, string expected)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        string? message = await child.StandardOutput.ReadLineAsync(timeout.Token);
        Assert.Equal(expected, message);
    }

    private static async Task KillAsync(Process child)
    {
        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (child.HasExited)
        {
            // The helper may exit between the liveness query and termination.
        }

        // Join the native handle: Windows can publish an exit code before releasing all process resources.
        // Cleanup must still complete if xUnit cancels the test itself.
        bool exited = await Task.Run(() => child.WaitForExit(30_000), CancellationToken.None);
        Assert.True(exited, "The database helper did not exit within the cleanup timeout.");
    }
}
