using System.Text;
using BenchmarkDotNet.Attributes;
using Incant.Base.Log;
using LogRecorder = Incant.Base.Log.Log;

namespace Incant.Benchmarks.Log;

[MemoryDiagnoser]
[LogBenchmarkJob(1)]
public class CliRenderingLogBenchmarks
{
    private const int EventsPerBatch = 64;

    [GlobalSetup]
    public void Setup()
    {
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(new CliLogSink(TextWriter.Null, colorMode: CliColorMode.Always));
        LogRecorder.Start(
            new LogOptions
            {
                QueueCapacityPerThread = 256,
                FlushInterval = TimeSpan.FromSeconds(30),
            });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (LogRecorder.IsRunning)
        {
            LogRecorder.Stop();
        }
    }

    [Benchmark(OperationsPerInvoke = EventsPerBatch)]
    public void ParseBuildTreeAndRenderBatch()
    {
        for (int index = 0; index < EventsPerBatch; ++index)
        {
            LogRecorder.Info(
                Text.Warning(),
                "{#Scope}Compile {Source} -> {Target}{/Scope}",
                Text.Important(),
                Param.Label("input.cpp"),
                Param.Important("output.obj"));
        }

        LogRecorder.Flush();
    }
}

[MemoryDiagnoser]
[LogBenchmarkJob(1)]
public class FileWritingLogBenchmarks
{
    private const int EventsPerBatch = 64;
    private string _directory = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Incant.Benchmarks", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(_directory, "benchmark.log");
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(new FileLogSink(path));
        LogRecorder.Start(
            new LogOptions
            {
                QueueCapacityPerThread = 256,
                FlushInterval = TimeSpan.FromSeconds(30),
            });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (LogRecorder.IsRunning)
        {
            LogRecorder.Stop();
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Benchmark(OperationsPerInvoke = EventsPerBatch)]
    public void WriteAndFlushBatch()
    {
        for (int index = 0; index < EventsPerBatch; ++index)
        {
            LogRecorder.Info("Compile {Source} -> {Target}", "input.cpp", "output.obj");
        }

        LogRecorder.Flush();
    }
}

[MemoryDiagnoser]
[LogBenchmarkJob(1)]
public class LogFileReaderBenchmarks
{
    private const int RecordsPerBatch = 128;
    private string _content = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        var builder = new StringBuilder();
        for (int index = 1; index <= RecordsPerBatch; ++index)
        {
            builder.Append("@1 2026-08-29T06:03:12.1234567Z [P1:T2] [INF] [Build]\n");
            builder.Append(">> Compile input.cpp\n");
            builder.Append(":: property Source: \"input.cpp\"\n\n");
        }

        _content = builder.ToString();
    }

    [Benchmark(OperationsPerInvoke = RecordsPerBatch)]
    public int ReadBatch()
    {
        using var reader = new StringReader(_content);
        int count = 0;
        foreach (LogFileRecord _ in LogFileReader.Read(reader))
        {
            ++count;
        }

        return count;
    }
}
