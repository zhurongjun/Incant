using System.Globalization;
using Incant.Base.Deps;

namespace Incant.Bench.Deps;

internal sealed class BenchmarkDataset : IDisposable
{
    internal BenchmarkDataset(int keyCount)
    {
        _root = Path.Combine(Program.ArtifactsPath, "data", Guid.NewGuid().ToString("N"));
        StorePath = Path.Combine(_root, "store");
        Directory.CreateDirectory(_root);
        var files = new string[256];
        for (int index = 0; index < files.Length; index++)
        {
            files[index] = Path.Combine(_root, $"file-{index:D3}.h");
            File.WriteAllText(files[index], "// Deterministic benchmark input.\n");
        }

        Items = new WorkItem[keyCount];
        for (int index = 0; index < keyCount; index++)
        {
            string[] dependencies = Enumerable.Range(0, 16)
                .Select(offset => files[(index + offset) % files.Length]).ToArray();
            Items[index] = new WorkItem($"object-{index:D6}", dependencies[..12], dependencies[12..]);
        }

        Reset();
    }

    internal string StorePath { get; }

    internal WorkItem[] Items { get; }

    internal void Reset()
    {
        if (Directory.Exists(StorePath))
        {
            Directory.Delete(StorePath, recursive: true);
        }

        Directory.CreateDirectory(StorePath);
    }

    internal Database Create(bool readOnly = false) =>
        new(Path.Combine(StorePath, "records.db"), new FileSnapshotCache(), readOnly: readOnly);

    internal void Seed()
    {
        Database database = Create();
        using DatabaseScope databaseScope = Database.OpenScoped(database);
        WriteAll(database, workers: 1, update: false);
    }

    internal void WriteAll(Database database, int workers, bool update)
    {
        ForEach(workers, item => Write(database, item, update));
    }

    internal void ReadAll(Database database, int workers)
    {
        ForEach(workers, item =>
        {
            if (database.IsOutdated(item.Key, item.Files, s_args))
            {
                throw new InvalidOperationException("A seeded record unexpectedly expired.");
            }
        });
    }

    internal void PrepareHistory(Database database)
    {
        // Stop exactly at the automatic-compaction boundary so the timed operation owns all compaction cost.
        int records = Math.Max(1000, Items.Length * 3);
        for (int index = 0; index < records; index++)
        {
            Write(database, Items[index % Items.Length], update: false, force: true);
        }
    }

    internal long GetStorageBytes() =>
        Directory.EnumerateFiles(StorePath, "*", SearchOption.AllDirectories)
            .Sum(static path => new FileInfo(path).Length);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Write(Database database, WorkItem item, bool update, bool force = false)
    {
        string[] args = update ? s_updatedArgs : s_args;
        var options = new CheckOptions { Force = force };
        bool changed = database.RunIfOutdated(item.Key, item.CaptureExternalFiles, item.Files, args, options);
        if (!changed)
        {
            throw new InvalidOperationException("The write workload did not update its record.");
        }
    }

    private void ForEach(int workers, Action<WorkItem> action)
    {
        if (workers == 1)
        {
            foreach (WorkItem item in Items)
            {
                action(item);
            }

            return;
        }

        // Fixed partitions avoid a per-record work-item allocation while bounding producer concurrency.
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
        {
            for (int index = worker; index < Items.Length; index += workers)
            {
                action(Items[index]);
            }
        });
    }

    private static readonly string[] s_args = ["-O2", "-std=c++20", "-DREVISION=0", "value,with\"escaping"];

    private static readonly string[] s_updatedArgs = ["-O2", "-std=c++20", "-DREVISION=1", "value,with\"escaping"];

    private readonly string _root;
}

internal sealed class WorkItem
{
    internal WorkItem(string key, string[] files, string[] externalFiles)
    {
        Key = key;
        Files = files;
        _externalFiles = externalFiles;
        CaptureExternalFiles = record => record.AddExternalFileRange(_externalFiles);
    }

    internal string Key { get; }

    internal string[] Files { get; }

    internal Action<Record> CaptureExternalFiles { get; }

    private readonly string[] _externalFiles;
}

internal static class StorageSizeReport
{
    internal static void Write(TextWriter writer)
    {
        writer.WriteLine("Keys,InitialBytes,UpdatedBytes,CompactedBytes");
        foreach (int keys in new[] { 64, 10_000 })
        {
            using var dataset = new BenchmarkDataset(keys);
            Database database = dataset.Create();
            using DatabaseScope databaseScope = Database.OpenScoped(database);
            dataset.WriteAll(database, workers: 1, update: false);
            long initial = dataset.GetStorageBytes();
            dataset.WriteAll(database, workers: 1, update: true);
            long updated = dataset.GetStorageBytes();
            database.Compact();
            long compacted = dataset.GetStorageBytes();
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{keys},{initial},{updated},{compacted}"));
        }
    }
}
