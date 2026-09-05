using BenchmarkDotNet.Attributes;
using Incant.Base.Deps;

namespace Incant.Bench.Deps;

/// <summary>Measures first writes and updates without including opening, setup, or final durable flushing.</summary>
[MemoryDiagnoser]
[DepsBenchmarkJob]
public class DatabaseWriteBenchmarks
{
    /// <summary>Gets or sets the number of dependency keys in one measured batch.</summary>
    [Params(64, 10_000)]
    public int Keys { get; set; }

    /// <summary>Gets or sets the maximum concurrent producer workers.</summary>
    [Params(1, 8)]
    public int Workers { get; set; }

    /// <summary>Creates the common input files outside measurement.</summary>
    [GlobalSetup]
    public void Setup() => _dataset = new BenchmarkDataset(Keys);

    /// <summary>Starts with an empty database for each insertion batch.</summary>
    [IterationSetup(Target = nameof(Insert))]
    public void PrepareInsert()
    {
        _dataset.Reset();
        _database = _dataset.Create();
        _database.Open();
    }

    /// <summary>Seeds one record per key; one later update per key remains below the compaction threshold.</summary>
    [IterationSetup(Target = nameof(Update))]
    public void PrepareUpdate()
    {
        _dataset.Reset();
        _dataset.Seed();
        _database = _dataset.Create();
        _database.Open();
        _dataset.ReadAll(_database, workers: 1);
    }

    /// <summary>Writes each key for the first time, including dependency validation and file snapshots.</summary>
    [Benchmark]
    public void Insert() => _dataset.WriteAll(_database, Workers, update: false);

    /// <summary>Updates all keys by changing one argument, including fresh file snapshots.</summary>
    [Benchmark]
    public void Update() => _dataset.WriteAll(_database, Workers, update: true);

    /// <summary>Requests the final durable flush outside the measured batch.</summary>
    [IterationCleanup]
    public void FinishBatch() => _database.Close();

    /// <summary>Removes only the temporary data created by this benchmark instance.</summary>
    [GlobalCleanup]
    public void Cleanup() => _dataset.Dispose();

    private BenchmarkDataset _dataset = null!;

    private Database _database = null!;
}
