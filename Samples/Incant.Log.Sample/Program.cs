using Incant.Base.Log;
using LogRecorder = Incant.Base.Log.Log;

namespace Incant.Log.Sample;

internal static class Program
{
    private static int Main()
    {
        string outputPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "Incant.Log.Sample.log"));

        ConfigureLogging(outputPath);
        try
        {
            RecordRuntimeState(outputPath);
            RecordLevelsAndCategories();
            RecordTemplateAndParameterFeatures();
            RecordTextAndCustomDecorators();
            RecordStructuredValue();
            RecordException();
            RecordFromMultipleThreads();
            DemonstrateRuntimeLevelFiltering();
            LogRecorder.Flush();
        }
        finally
        {
            if (LogRecorder.IsRunning)
            {
                LogRecorder.Stop();
            }
        }

        int recordCount = CountFileRecords(outputPath);
        Console.WriteLine($"Parsed {recordCount} records from the file sink.");
        Console.WriteLine(outputPath);
        return 0;
    }

    private static void ConfigureLogging(string outputPath)
    {
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(new CliLogSink(colorMode: CliColorMode.Auto));
        LogRecorder.AddSink(new FileLogSink(outputPath, LogLevel.Trace));
        LogRecorder.Start(
            new LogOptions
            {
                QueueCapacityPerThread = 256,
                FlushInterval = TimeSpan.FromSeconds(1),
            });
    }

    private static void RecordRuntimeState(string outputPath)
    {
        LogRecorder.Info(
            LogCategory.General,
            "Logger running: {IsRunning}; trace enabled: {TraceEnabled}; sinks: {SinkCount}; file: {Path}",
            LogRecorder.IsRunning,
            LogRecorder.IsEnabled(LogLevel.Trace),
            LogRecorder.Sinks.Count,
            Param.Muted(outputPath));
    }

    private static void RecordLevelsAndCategories()
    {
        LogRecorder.Trace(LogCategory.General, "Trace-level diagnostic");
        LogRecorder.Debug(LogCategory.Dependency, "Resolved dependency {Dependency}", Param.Label("Incant.Base"));
        LogRecorder.Info(LogCategory.Build, "Building target {Target}", Param.Important("Incant"));
        LogRecorder.Warning(LogCategory.Cache, "Cache entry {Key} is stale", Param.Warning("object-cache"));
        LogRecorder.Error(LogCategory.IO, "Could not read optional file {Path}", Param.Error("missing.props"));
        LogRecorder.Fatal(LogCategory.Process, "Simulated fatal report for process {ProcessId}", Environment.ProcessId);
        LogRecorder.Info(new LogCategory("Sample"), "Custom categories use the same structured API");
    }

    private static void RecordTemplateAndParameterFeatures()
    {
        LogRecorder.Info(
            LogCategory.Build,
            "Progress {Progress,8:P1}; code {Code:X4}; escaped {{braces}}",
            0.875,
            42);

        LogRecorder.Debug(
            "Pipeline {Configure} -> {Compile} -> {Link} -> {Package} -> {Complete}",
            "configure",
            "compile",
            "link",
            "package",
            "complete");

        LogRecorder.Info(
            "Parameter roles: {Plain} {Muted} {Important} {Warning} {Error} {Label}",
            Param.Plain("plain"),
            Param.Muted("muted"),
            Param.Important("important"),
            Param.Warning("warning"),
            Param.Error("error"),
            Param.Label("label"));
    }

    private static void RecordTextAndCustomDecorators()
    {
        LogRecorder.Info(
            "Text roles: {#Plain}plain{/Plain} {#Muted}muted{/Muted} "
                + "{#Important}important{/Important} {#Warning}warning{/Warning} "
                + "{#Error}error{/Error} {#Label}label{/Label}",
            Text.Plain(),
            Text.Muted(),
            Text.Important(),
            Text.Warning(),
            Text.Error(),
            Text.Label());

        TextDecorator rootDecorator = new SampleTextDecorator("root", Text.Plain());
        TextDecorator scopeDecorator = new SampleTextDecorator("scope", Text.Warning());
        ParamDecorator parameterDecorator = new SampleParamDecorator(
            "parameter",
            Param.Important("Incant.Log.Sample"));
        LogRecorder.Info(
            LogCategory.Build,
            rootDecorator,
            "{#Step}Custom decorators preserve the build target {Target}{/Step}",
            scopeDecorator,
            parameterDecorator);
    }

    private static void RecordStructuredValue()
    {
        var snapshot = new
        {
            Project = "Incant",
            Configuration = "Release",
            Sources = new[] { "Command.cs", "Options.cs" },
            Succeeded = true,
        };
        ParamDecorator structured = Param.Important(
            new SampleParamDecorator("structured", Param.Structured(snapshot)));

        LogRecorder.Info(LogCategory.Build, "Structured build state {State}", structured);
    }

    private static void RecordException()
    {
        try
        {
            throw new InvalidOperationException("The simulated linker rejected one input.");
        }
        catch (InvalidOperationException exception)
        {
            LogRecorder.Error(
                LogCategory.Build,
                Text.Error(),
                exception,
                "Build action {Action} failed for {Target}",
                Param.Label("Link"),
                Param.Important("Incant"));
        }
    }

    private static void RecordFromMultipleThreads()
    {
        Thread[] workers =
        [
            CreateWorker(1, "Command.cs"),
            CreateWorker(2, "Options.cs"),
        ];

        foreach (Thread worker in workers)
        {
            worker.Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }
    }

    private static Thread CreateWorker(int workerIndex, string sourceFile)
    {
        return new Thread(
            () =>
            {
                LogRecorder.Info(
                    LogCategory.Scheduler,
                    "Worker {Worker} compiling {Source} on managed thread {ThreadId}",
                    Param.Label(workerIndex),
                    Param.Important(sourceFile),
                    Environment.CurrentManagedThreadId);
                Thread.Sleep(workerIndex * 5);
                LogRecorder.Debug(LogCategory.Process, "Worker {Worker} completed", workerIndex);
            })
        {
            Name = $"Log sample worker {workerIndex}",
        };
    }

    private static void DemonstrateRuntimeLevelFiltering()
    {
        LogRecorder.MinimumLevel = LogLevel.Warning;
        LogRecorder.Info("This event is intentionally filtered out.");
        LogRecorder.Warning(
            "Minimum level is {MinimumLevel}; debug enabled: {DebugEnabled}",
            LogRecorder.MinimumLevel,
            LogRecorder.IsEnabled(LogLevel.Debug));

        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.Info("Minimum level restored to {MinimumLevel}", LogRecorder.MinimumLevel);
    }

    private static int CountFileRecords(string outputPath)
    {
        using StreamReader reader = File.OpenText(outputPath);
        return LogFileReader.Read(reader).Count();
    }

    private sealed class SampleTextDecorator(string name, TextDecorator? next) : TextDecorator(next)
    {
        internal string Name { get; } = name;
    }

    private sealed class SampleParamDecorator(string name, object? next) : ParamDecorator(next)
    {
        internal string Name { get; } = name;
    }
}
