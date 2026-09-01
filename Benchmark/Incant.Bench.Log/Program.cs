using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Incant.Bench.Log;

internal static class Program
{
    private static void Main(string[] arguments)
    {
        string artifactsPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "build",
                "benchmark",
                "log"));
        IConfig config = ManualConfig
            .Create(DefaultConfig.Instance)
            .WithArtifactsPath(artifactsPath);

        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(arguments, config);
    }
}
