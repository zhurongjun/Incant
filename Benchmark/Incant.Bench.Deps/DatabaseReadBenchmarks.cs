using BenchmarkDotNet.Attributes;
using Incant.Base.Deps;

namespace Incant.Bench.Deps;

/// <summary>Separately measures opening a persisted database and querying an already loaded database.</summary>
[MemoryDiagnoser]
[DepsBenchmarkJob]
public class DatabaseReadBenchmarks
{
    /// <summary>Gets or sets the number of dependency keys in one measured batch.</summary>
    [Params(64, 10_000)]
    public int Keys { get; set; }

    /// <summary>Gets or sets the maximum concurrent query workers.</summary>
    [Params(1, 8)]
    public int Workers { get; set; }

    /// <summary>Creates stable records and primes the warm-query metadata cache outside measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _dataset = new BenchmarkDataset(Keys);
        _dataset.Seed();
        _warmDatabase = _dataset.Create(readOnly: true);
        _warmDatabase.Open();
        _dataset.ReadAll(_warmDatabase, Workers);
    }

    /// <summary>Opens and checks all records using a fresh metadata cache; the OS file cache is not evicted.</summary>
    [Benchmark]
    public void OpenAndRead()
    {
        Database database = _dataset.Create(readOnly: true);
        using DatabaseScope databaseScope = Database.OpenScoped(database);
        _dataset.ReadAll(database, Workers);
    }

    /// <summary>Checks the same records using the already open database and populated metadata cache.</summary>
    [Benchmark]
    public void WarmRead() => _dataset.ReadAll(_warmDatabase, Workers);

    /// <summary>Releases the snapshot and temporary benchmark data.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _warmDatabase.Close();
        _dataset.Dispose();
    }

    private BenchmarkDataset _dataset = null!;

    private Database _warmDatabase = null!;
}
