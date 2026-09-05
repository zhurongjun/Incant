using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Incant.Bench.Deps;

internal static class Program
{
    private static void Main(string[] arguments)
    {
        if (arguments is ["--sizes"])
        {
            StorageSizeReport.Write(Console.Out);
            return;
        }

        IConfig config = ManualConfig.Create(DefaultConfig.Instance).WithArtifactsPath(ArtifactsPath);
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(arguments, config);
    }

    internal static string ArtifactsPath { get; } = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "build", "benchmark", "deps"));
}
