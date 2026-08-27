using SB.Core;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Channels;

namespace SB.Capabilities.BuildScripts;

internal static class BuildScriptDiscovery
{
    private const string BuildScriptFileName = "build.cs";
    private const int MinWorkerCount = 4;
    private const int MaxWorkerCount = 16;

    public static ImmutableArray<string> DiscoverBuildScriptPaths(string projectRoot, IReadOnlyList<string> roots)
    {
        var workerCount = WorkerCount();
        var comparer = PathComparer();
        var allPaths = new List<string>();

        BuildTrace.Mark("BuildScriptDiscovery.worker_count", workerCount.ToString());

        foreach (var root in roots)
        {
            var fullRoot = Path.GetFullPath(Path.Combine(projectRoot, root));
            if (!Directory.Exists(fullRoot))
            {
                BuildTrace.Mark("BuildScriptDiscovery.root.skip_missing", root);
                continue;
            }

            var result = DiscoverRootAsync(root, fullRoot, workerCount).GetAwaiter().GetResult();
            allPaths.AddRange(result.Paths);
            BuildTrace.Mark(
                "BuildScriptDiscovery.root",
                $"{root}: dirs={result.DirectoryCount} scripts={result.Paths.Length} skipped_reparse={result.SkippedReparsePointCount}");
        }

        return allPaths
            .Distinct(comparer)
            .OrderBy(path => NormalizeRelativePath(projectRoot, path), StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static async Task<RootDiscoveryResult> DiscoverRootAsync(string rootName, string rootDirectory, int workerCount)
    {
        var stopwatch = Stopwatch.StartNew();
        using var trace = BuildTrace.Scope($"BuildScriptDiscovery.root.{rootName.Replace('/', '.')}");
        using var cancel = new CancellationTokenSource();

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        var discoveredPaths = new ConcurrentBag<string>();
        var errors = new ConcurrentQueue<TaskFatalError>();
        var pendingDirectoryCount = 0;
        long visitedDirectoryCount = 0;
        long skippedReparsePointCount = 0;

        void EnqueueDirectory(string directory)
        {
            if (cancel.IsCancellationRequested)
                return;

            Interlocked.Increment(ref pendingDirectoryCount);
            if (!channel.Writer.TryWrite(directory) &&
                Interlocked.Decrement(ref pendingDirectoryCount) == 0)
            {
                channel.Writer.TryComplete();
            }
        }

        void CompleteDirectory()
        {
            if (Interlocked.Decrement(ref pendingDirectoryCount) == 0)
                channel.Writer.TryComplete();
        }

        void ReportError(string directory, Exception exception)
        {
            errors.Enqueue(new TaskFatalError(
                $"Failed to discover build scripts under {rootName}.",
                $"Directory: {directory}{Environment.NewLine}{exception}"));
            cancel.Cancel();
            channel.Writer.TryComplete();
        }

        void VisitDirectory(string directory)
        {
            Interlocked.Increment(ref visitedDirectoryCount);

            var buildScript = Path.Combine(directory, BuildScriptFileName);
            if (File.Exists(buildScript))
                discoveredPaths.Add(Path.GetFullPath(buildScript));

            foreach (var childDirectory in new DirectoryInfo(directory).EnumerateDirectories())
            {
                if ((childDirectory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Interlocked.Increment(ref skippedReparsePointCount);
                    continue;
                }

                EnqueueDirectory(childDirectory.FullName);
            }
        }

        async Task RunWorker()
        {
            try
            {
                while (await channel.Reader.WaitToReadAsync(cancel.Token).ConfigureAwait(false))
                {
                    while (channel.Reader.TryRead(out var directory))
                    {
                        if (cancel.IsCancellationRequested)
                            return;

                        try
                        {
                            VisitDirectory(directory);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            ReportError(directory, ex);
                            return;
                        }
                        finally
                        {
                            CompleteDirectory();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
            }
        }

        EnqueueDirectory(rootDirectory);

        var workers = Enumerable
            .Range(0, workerCount)
            .Select(_ => Task.Run(RunWorker))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);

        if (errors.TryPeek(out var error))
            throw error;

        stopwatch.Stop();
        BuildTrace.Mark(
            "BuildScriptDiscovery.root.elapsed",
            $"{rootName}: {stopwatch.Elapsed.TotalMilliseconds:F3}ms");

        return new RootDiscoveryResult(
            discoveredPaths.ToImmutableArray(),
            Interlocked.Read(ref visitedDirectoryCount),
            Interlocked.Read(ref skippedReparsePointCount));
    }

    private static int WorkerCount()
    {
        var workerCount = Environment.ProcessorCount;
        var overrideValue = Environment.GetEnvironmentVariable("SB_BUILD_SCRIPT_DISCOVERY_WORKERS");
        if (!string.IsNullOrWhiteSpace(overrideValue) &&
            int.TryParse(overrideValue, out var parsed) &&
            parsed > 0)
        {
            workerCount = parsed;
        }
        return Math.Clamp(workerCount, MinWorkerCount, MaxWorkerCount);
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string NormalizeRelativePath(string projectRoot, string path) =>
        Path.GetRelativePath(projectRoot, path).Replace('\\', '/');

    private readonly record struct RootDiscoveryResult(
        ImmutableArray<string> Paths,
        long DirectoryCount,
        long SkippedReparsePointCount);
}
