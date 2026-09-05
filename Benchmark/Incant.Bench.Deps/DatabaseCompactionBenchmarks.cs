using BenchmarkDotNet.Attributes;
using Incant.Base.Deps;

namespace Incant.Bench.Deps;

/// <summary>Measures durable compaction independently from ordinary append throughput.</summary>
[MemoryDiagnoser]
[DepsBenchmarkJob]
public class DatabaseCompactionBenchmarks
{
    /// <summary>Gets or sets the number of live keys retained by compaction.</summary>
    [Params(64, 10_000)]
    public int Keys { get; set; }

    /// <summary>Creates common dependency inputs.</summary>
    [GlobalSetup]
    public void Setup() => _dataset = new BenchmarkDataset(Keys);

    /// <summary>Builds obsolete history without crossing the automatic-compaction threshold.</summary>
    [IterationSetup]
    public void Prepare()
    {
        _dataset.Reset();
        _database = _dataset.Create();
        _database.Open();
        _dataset.PrepareHistory(_database);
    }

    /// <summary>Rewrites live records, durably flushes, and atomically replaces the database.</summary>
    [Benchmark]
    public void Compact() => _database.Compact();

    /// <summary>Closes the compacted database.</summary>
    [IterationCleanup]
    public void FinishBatch() => _database.Close();

    /// <summary>Removes the benchmark's temporary data.</summary>
    [GlobalCleanup]
    public void Cleanup() => _dataset.Dispose();

    private BenchmarkDataset _dataset = null!;

    private Database _database = null!;
}
