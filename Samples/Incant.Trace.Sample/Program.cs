using System.Text.Json;
using Incant.Base.Trace;
using TraceRecorder = Incant.Base.Trace.Trace;

namespace Incant.Trace.Sample;

internal static class Program
{
    private const ulong DetailedFlowId = 0xfedcba9876543210UL;

    private static int Main(string[] arguments)
    {
        string outputPath = ResolveOutputPath(arguments);

        TraceRecorder.Start(TraceCategory.All);
        TraceCapture capture;
        try
        {
            VerifyTraceState();
            RecordInstantAndCounterEvents();
            RecordFastCorrelationEvents();
            SimulateBuild();
            capture = TraceRecorder.Stop();
        }
        finally
        {
            if (TraceRecorder.IsRunning)
            {
                TraceRecorder.Stop();
            }
        }

        WriteCapture(outputPath, capture);
        Console.WriteLine($"Wrote {capture.Events.Length} events from {capture.Threads.Length} threads.");
        Console.WriteLine(outputPath);
        return 0;
    }

    private static string ResolveOutputPath(string[] arguments)
    {
        if (arguments.Length > 1)
        {
            throw new ArgumentException("Expected zero arguments or one output file path.", nameof(arguments));
        }

        string path = arguments.Length == 1
            ? arguments[0]
            : Path.Combine(
                Environment.CurrentDirectory,
                "build",
                "samples",
                "trace",
                "Incant.Trace.Sample.json");
        return Path.GetFullPath(path);
    }

    private static void VerifyTraceState()
    {
        if (!TraceRecorder.IsRunning
            || !TraceRecorder.IsEnabled(TraceCategory.Build)
            || TraceRecorder.IsEnabled(TraceCategory.None))
        {
            throw new InvalidOperationException("The trace session was not initialized correctly.");
        }
    }

    private static void RecordInstantAndCounterEvents()
    {
        TraceRecorder.Event(TraceCategory.General, "Thread instant", TraceInstantScope.Thread);
        TraceRecorder.Event(TraceCategory.General, "Process instant", TraceInstantScope.Process);
        TraceRecorder.Event(TraceCategory.General, "Global instant", TraceInstantScope.Global);
        TraceRecorder.EventSlow(
            TraceCategory.Process,
            "Process {ProcessId} on {MachineName}",
            Environment.ProcessId,
            Environment.MachineName);
        TraceRecorder.EventSlow(
            TraceCategory.General,
            "Profiler event 中文: {Message}",
            TraceInstantScope.Process,
            "Arguments are snapshotted immediately.");
        TraceRecorder.EventSlow(TraceCategory.General, "Scalar argument {Value}", 42);
        TraceRecorder.EventSlow(
            TraceCategory.General,
            "Array argument {@Stages}",
            new[] { "configure", "build", "link" });
        TraceRecorder.EventSlow(TraceCategory.General, "Null argument {Value}", null);

        TraceRecorder.Counter(TraceCategory.Build, "Pending actions", -1L);
        TraceRecorder.Counter(TraceCategory.IO, "Processed bytes", 4096UL);
        TraceRecorder.Counter(TraceCategory.Cache, "Cache hit ratio", 0.75d);
        TraceRecorder.CounterSlow(
            TraceCategory.Build,
            "Build queue has {Running} running and {Waiting} waiting",
            2,
            3);
    }

    private static void RecordFastCorrelationEvents()
    {
        ulong asyncId = TraceRecorder.CreateId();
        TraceRecorder.AsyncBegin(TraceCategory.Scheduler, "Fast async operation", asyncId);
        TraceRecorder.AsyncEvent(TraceCategory.Scheduler, "Fast async operation", asyncId);
        TraceRecorder.AsyncEnd(TraceCategory.Scheduler, "Fast async operation", asyncId);

        ulong flowId = TraceRecorder.CreateId();
        TraceRecorder.FlowStart(TraceCategory.Dependency, "Fast flow", flowId);
        TraceRecorder.FlowStep(TraceCategory.Dependency, "Fast flow", flowId);
        TraceRecorder.FlowEnd(TraceCategory.Dependency, "Fast flow", flowId);
    }

    private static void SimulateBuild()
    {
        using TraceScope buildScope = TraceRecorder.ScopeSlow(
            TraceCategory.Build,
            "Build project {Project} in {Configuration}",
            "Incant.Trace.Sample",
            "Release");

        TraceRecorder.FlowStartSlow(
            TraceCategory.Dependency,
            "Build pipeline entered {Stage}",
            DetailedFlowId,
            "Dependency discovery");

        using (TraceRecorder.Scope(TraceCategory.Dependency, "Discover dependencies"))
        {
            Thread.Sleep(5);
            TraceRecorder.EventSlow(
                TraceCategory.Dependency,
                "Discovered {Count} dependencies",
                4);
        }

        TraceRecorder.FlowStepSlow(
            TraceCategory.Build,
            "Build pipeline entered {Stage}",
            DetailedFlowId,
            "Compilation");

        SimulateParallelCompilation();

        TraceRecorder.FlowStepSlow(
            TraceCategory.Build,
            "Build pipeline entered {Stage}",
            DetailedFlowId,
            "Link");

        using (TraceRecorder.Scope(TraceCategory.Build | TraceCategory.IO, "Link executable"))
        {
            Thread.Sleep(8);
        }

        TraceRecorder.FlowEndSlow(
            TraceCategory.Build,
            "Build pipeline entered {Stage}",
            DetailedFlowId,
            "Completed");
    }

    private static void SimulateParallelCompilation()
    {
        ulong asyncId = TraceRecorder.CreateId();
        TraceRecorder.AsyncBeginSlow(
            TraceCategory.Scheduler,
            "Parallel compilation entered {Stage}",
            asyncId,
            "Configured 2 workers");

        Thread[] workers =
        [
            CreateCompileWorker(1, "Command.cs", 12),
            CreateCompileWorker(2, "Options.cs", 16),
        ];

        foreach (Thread worker in workers)
        {
            worker.Start();
        }

        TraceRecorder.AsyncEventSlow(
            TraceCategory.Scheduler,
            "Parallel compilation entered {Stage}",
            asyncId,
            "Workers started");

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        TraceRecorder.AsyncEndSlow(
            TraceCategory.Scheduler,
            "Parallel compilation entered {Stage}",
            asyncId,
            "Workers joined");
    }

    private static Thread CreateCompileWorker(int workerIndex, string sourceFile, int durationMilliseconds)
    {
        return new Thread(
            () =>
            {
                using TraceScope scope = TraceRecorder.ScopeSlow(
                    TraceCategory.Build | TraceCategory.Process,
                    "Compile {SourceFile} on worker {Worker}",
                    sourceFile,
                    workerIndex);
                TraceRecorder.EventSlow(
                    TraceCategory.IO,
                    "Read source file {SourceFile}",
                    sourceFile);
                Thread.Sleep(durationMilliseconds);
                TraceRecorder.Event(TraceCategory.Cache, "Store object cache");
            })
        {
            Name = $"Trace sample worker {workerIndex}",
        };
    }

    private static void WriteCapture(string outputPath, TraceCapture capture)
    {
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using FileStream stream = File.Create(outputPath);
        using var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Indented = true,
            });
        GoogleTraceJsonWriter.Write(writer, capture);
        writer.Flush();
    }
}
