using Cli = SB.Cli;
using SB;
using SB.Core;
using Serilog;
using Serilog.Events;
using System.Diagnostics;
namespace SB;

public class RunCommand : CommandBase
{
    override protected bool DumpCounters => false;

    [Cli.RegisterCmd(Name = "run", ShortName = 'r', Help = "Build and run target", Usage = "SB run <target> [args...]\nSB r <target> [args...]")]
    public static object RegisterCommand() => new RunCommand();

    [Cli.RestOptions(Help = "Target and arguments passed to the executable", IsRequired = true)]
    public string[] RunArgs { get; set; } = [];

    public override int OnExecute()
    {
        using var trace = BuildTrace.Scope("RunCommand.OnExecute");
        var Instance = CreateBuildInstance();
        IToolchain Toolchain;
        using (BuildTrace.Scope("RunCommand.GetToolchain"))
        {
            Toolchain = GetToolchain(Instance);
        }
        var targetName = RunArgs[0];

        // find target
        var target = BuildTrace.Measure<Target?>("RunCommand.FindTarget", () => Instance.GetTarget(targetName));
        if (target == null)
        {
            // collect candidate targets
            var candidates = Instance.AllTargets.Values
                .Select(t => t.Name)
                .Where(n => n.Contains(targetName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n)
                .ToList();

            // join candidates by "\n  -"
            if (candidates.Count > 0)
            {
                var candidateList = string.Join("\n  - ", candidates);
                Log.Error("Target {Target} not found. Similar targets:\n  - {Candidates}", targetName, candidateList);
            }
            else
            {
                Log.Error("Target {Target} not found.", targetName);
            }


            return -1;
        }

        // build target
        BuildTrace.Measure("RunCommand.AddBeforeBuildEmitter", Instance.AddBeforeBuildEmitter);
        BuildTrace.Measure("RunCommand.AddCppPreparationEmitters", Instance.AddCppPreparationEmitters);
        BuildTrace.Measure("RunCommand.AddEngineTaskEmitters", () => Instance.AddEngineTaskEmitters(Toolchain));
        BuildTrace.Measure("RunCommand.RunBuild", () => Instance.RunBuild(targetName));

        // solve target path
        var linkResult = BuildTrace.Measure<List<LinkResult>>("RunCommand.ResolveLinkResult", () => Instance.Artifacts
                .Where(a => a is LinkResult)
                .Select(a => (LinkResult)a)
                .Where(l => l.Target == target)
                .ToList());
        if (linkResult.Count == 0)
        {
            Log.Error("Failed to find link result for target {Target}", targetName);
            return -1;
        }

        // run target
        var exePath = linkResult[0].TargetFile;
        var exeArgs = string.Join(' ', RunArgs[1..]);
        if (!OperatingSystem.IsWindows() && File.Exists(exePath))
        {
            var Mode = File.GetUnixFileMode(exePath);
            if (!Mode.HasFlag(UnixFileMode.UserExecute))
                File.SetUnixFileMode(exePath, UnixFileMode.UserExecute);
        }
        try
        {
            Process P = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = exeArgs,
                    WorkingDirectory = Directory.GetParent(exePath)!.FullName,
                    // shell execute
                    CreateNoWindow = false,
                    UseShellExecute = false,
                    // redirect stream
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };

            // add output processor
            P.OutputDataReceived += (sender, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
            P.ErrorDataReceived += (sender, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\nRunning {exePath} {exeArgs}:\n");
            Console.ResetColor();

            // start up
            BuildTrace.Measure("RunCommand.Process.Start", () => P.Start());
            BuildTrace.Measure("RunCommand.Process.BeginOutputReadLine", P.BeginOutputReadLine);
            BuildTrace.Measure("RunCommand.Process.BeginErrorReadLine", P.BeginErrorReadLine);

            // 在 .NET 中，当使用 BeginOutputReadLine() 和 BeginErrorReadLine() 时，输出是异步读取的。进程可能已经退出了，但异步读取线程可能还在处理缓冲区中的数据。
            // 需要在 WaitForExit(timeout) 返回 true 后，再调用无参数的 WaitForExit() 来确保所有异步读取操作完成：
            BuildTrace.Measure("RunCommand.Process.WaitForExit", P.WaitForExit);

            Console.ForegroundColor = P.ExitCode == 0 ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"\nProcess exited with code {P.ExitCode}");
            Console.ResetColor();

            return P.ExitCode;
        }
        catch (Exception e)
        {
            throw new TaskFatalError($"Failed to run process {exePath} with arguments {exeArgs}", e.Message);
        }
    }
};
