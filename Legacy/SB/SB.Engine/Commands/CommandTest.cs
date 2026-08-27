using Cli = SB.Cli;
using SB;
using SB.Core;
using Serilog;
using System.Diagnostics;

namespace SB;

public class TestCommand : CommandBase
{
    [Cli.Option(Name = "verbose", ShortName = 'v', Help = "Print test process stdout/stderr", IsRequired = false)]
    public new bool Verbose { get; set; } = false;

    [Cli.Option(Name = "timeout", Help = "Per-test timeout in milliseconds. 0 disables timeout.", IsRequired = false)]
    public int Timeout { get; set; } = 0;

    [Cli.Option(Name = "target", Help = "Run a single test target.", IsRequired = false)]
    public string? SingleTarget { get; set; }

    public override int OnExecute()
    {
        // 先做参数合法性检查，再进入构建系统初始化，避免无效参数触发昂贵的 target 加载。
        if (Timeout < 0)
        {
            Log.Error("--timeout must be greater than or equal to 0.");
            return 1;
        }

        var instance = CreateTestBuildInstance();

        // 单 target 模式需要透传测试进程返回码，所以与全量聚合模式分开处理。
        if (!string.IsNullOrWhiteSpace(SingleTarget))
            return RunSingleTestTarget(instance, SingleTarget);

        return RunAllTestTargets(instance);
    }

    private BuildInstance CreateTestBuildInstance()
    {
        // test 命令需要先构建 C++ 测试程序，因此只注册通用 C++ 构建 emitter。
        var instance = CreateBuildInstance();
        var toolchain = GetToolchain(instance);
        instance.AddCppPreparationEmitters();
        instance.AddEngineTaskEmitters(toolchain);
        return instance;
    }

    private int RunAllTestTargets(BuildInstance instance)
    {
        // 全量模式只以带 Test 标签的 C++ 可执行目标作为构建根，并串行执行。
        var selectedTargetNames = SelectTestBuildRootNames(instance);
        var buildResult = instance.RunBuildTargets(selectedTargetNames);
        if (buildResult != 0)
            return buildResult;

        var testTargets = CollectTestTargets(instance, selectedTargetNames);
        if (!testTargets.Any())
        {
            Log.Warning("No test targets found.");
            return -1;
        }

        WriteRunHeader(testTargets.Count, "all");

        var failCount = 0;
        var timeoutCount = 0;
        foreach (var target in testTargets)
        {
            // 非 verbose 时只在失败或超时时展开进程输出，避免成功用例刷屏。
            var result = RunTestTarget(target, Timeout);
            if (Verbose || result.Status != ETestStatus.Pass)
                WriteProcessOutput(result);

            WriteTestStatus(result, Timeout);
            if (result.Status == ETestStatus.Fail)
                failCount++;
            else if (result.Status == ETestStatus.Timeout)
                timeoutCount++;
        }

        if (failCount > 0)
            Log.Error("{FailCount} test target(s) failed.", failCount);
        if (timeoutCount > 0)
            Log.Error("{TimeoutCount} test target(s) timed out.", timeoutCount);
        return failCount > 0 || timeoutCount > 0 ? 1 : 0;
    }

    private int RunSingleTestTarget(BuildInstance instance, string targetName)
    {
        // 单 target 模式是定位问题用入口：只构建一个目标，并始终打印该目标的完整输出。
        var target = ResolveSingleTestTarget(instance, targetName);
        if (target is null)
            return 1;

        var selectedTargetNames = new[] { target.Name };
        var buildResult = instance.RunBuildTargets(selectedTargetNames);
        if (buildResult != 0)
            return buildResult;

        var testTargets = CollectTestTargets(instance, selectedTargetNames);
        if (!testTargets.Any())
        {
            Log.Error("Failed to find link result for test target {Target}.", target.Name);
            return -1;
        }

        WriteRunHeader(1, target.Name);

        var result = RunTestTarget(testTargets[0], Timeout);
        WriteProcessOutput(result);
        WriteTestStatus(result, Timeout);
        // 这里必须透传测试程序返回码，方便脚本精确判断该 target 的运行结果。
        return result.ExitCode;
    }

    private void WriteRunHeader(int targetCount, string targetName)
    {
        Log.Information("Found {Count} test target(s)", targetCount);
        Log.Information("Test target: {Target}", targetName);
        Log.Information("Test timeout: {Timeout} ms ({Mode})", Timeout, Timeout == 0 ? "disabled" : "enabled");
    }

    private static Target? ResolveSingleTestTarget(BuildInstance instance, string targetName)
    {
        // --target 只接受带 Test 标签的 C++ 可执行目标，避免把普通程序当作测试运行。
        var target = instance.GetTarget(targetName);
        if (target is null)
        {
            Log.Error("Test target {Target} does not exist.", targetName);
            LogSimilarTestTargets(instance, targetName);
            return null;
        }

        if (!RegisteredAssetHelpers.IsCppTestProgram(target))
        {
            Log.Error("Target {Target} is not a C++ test program target.", targetName);
            return null;
        }

        return target;
    }

    private static string[] SelectTestBuildRootNames(BuildInstance instance)
    {
        // test target 必须是带 Test 标签的 C++ 可执行目标。
        return instance.AllTargets.Values
            .Where(RegisteredAssetHelpers.IsCppTestProgram)
            .OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
            .Select(target => target.Name)
            .ToArray();
    }

    private static void LogSimilarTestTargets(BuildInstance instance, string targetName)
    {
        // 给输错 target 名的场景提供轻量提示，不做模糊匹配替代用户选择。
        var candidates = instance.AllTargets.Values
            .Where(RegisteredAssetHelpers.IsCppTestProgram)
            .Select(target => target.Name)
            .Where(name => name.Contains(targetName, StringComparison.OrdinalIgnoreCase) ||
                           targetName.Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
        if (candidates.Length > 0)
            Log.Error("Similar test targets:\n  - {Candidates}", string.Join("\n  - ", candidates));
    }

    private static List<TestTarget> CollectTestTargets(BuildInstance instance, IReadOnlyCollection<string> selectedTargetNames)
    {
        // 构建完成后从 LinkResult 中取真实可执行文件路径，未被本次选择的 target 不参与运行。
        var selectedNames = selectedTargetNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return instance.Artifacts
            .OfType<LinkResult>()
            .Where(link => RegisteredAssetHelpers.IsCppTestProgram(link.Target) &&
                           selectedNames.Contains(link.Target.Name) &&
                           !string.IsNullOrEmpty(link.TargetFile))
            .OrderBy(link => link.Target.Name, StringComparer.OrdinalIgnoreCase)
            .Select(link => new TestTarget(
                link.Target.Name,
                link.TargetFile))
            .ToList();
    }

    private static TestResult RunTestTarget(TestTarget target, int timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        ProcessOptions options = new()
        {
            WorkingDirectory = Path.GetDirectoryName(target.FilePath),
            EnableTimeout = timeout > 0,
            TimeoutMilliseconds = timeout > 0 ? timeout : ProcessOptions.Default.Value.TimeoutMilliseconds
        };
        var exitCode = BuildInstance.RunProcess(target.FilePath, "", out var stdout, out var stderr, options);
        sw.Stop();

        // RunProcess 在超时时把 stderr 置为 TimeOut；其它非零退出都按普通失败处理。
        var status = stderr.Trim().Equals("TimeOut", StringComparison.OrdinalIgnoreCase)
            ? ETestStatus.Timeout
            : exitCode == 0
                ? ETestStatus.Pass
                : ETestStatus.Fail;

        return new TestResult
        {
            Name = target.Name,
            FilePath = target.FilePath,
            ExitCode = exitCode,
            Status = status,
            Stdout = stdout,
            Stderr = stderr,
            Seconds = sw.ElapsedMilliseconds / 1000.0f
        };
    }

    private static void WriteProcessOutput(TestResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Stdout))
            Log.Information("Test {TargetName} stdout:\n{Stdout}", result.Name, result.Stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(result.Stderr))
            Log.Information("Test {TargetName} stderr:\n{Stderr}", result.Name, result.Stderr.TrimEnd());
    }

    private static void WriteTestStatus(TestResult result, int timeout)
    {
        switch (result.Status)
        {
            case ETestStatus.Pass:
                Log.Information("[PASS] {TargetName} ({Seconds}s)", result.Name, result.Seconds);
                break;
            case ETestStatus.Fail:
                Log.Error("[FAIL] {TargetName} (exit {Code}, {Seconds}s)", result.Name, result.ExitCode, result.Seconds);
                break;
            case ETestStatus.Timeout:
                Log.Error("[TIMEOUT] {TargetName} ({Seconds}s, limit {Timeout} ms)", result.Name, result.Seconds, timeout);
                break;
        }
    }

    private sealed record TestTarget(string Name, string FilePath);

    private enum ETestStatus
    {
        Pass,
        Fail,
        Timeout
    }

    private sealed class TestResult
    {
        public string Name { get; init; } = "";
        public string FilePath { get; init; } = "";
        public int ExitCode { get; init; }
        public ETestStatus Status { get; init; }
        public string Stdout { get; init; } = "";
        public string Stderr { get; init; } = "";
        public float Seconds { get; init; }
    }

    [Cli.RegisterCmd(Name = "test", ShortName = 't', Help = "Run tests", Usage = "SB test [options]\nSB t [options]")]
    public static object RegisterCommand() => new TestCommand();
}
