using Cli = SB.Cli;
using SB;
using SB.Core;
using Serilog;

namespace SB;

public class BuildCommand : BuildCommandBase
{
    [Cli.SubCmd(Name = "tools", Help = "Build tool targets", Usage = "SB build tools [options]")]
    public BuildToolsCommand Tools { get; set; } = new();

    [Cli.SubCmd(Name = "tests", Help = "Build test targets", Usage = "SB build tests [options]")]
    public BuildTestsCommand Tests { get; set; } = new();

    [Cli.SubCmd(Name = "benches", Help = "Build benchmark targets", Usage = "SB build benches [options]")]
    public BuildBenchesCommand Benches { get; set; } = new();

    [Cli.SubCmd(Name = "all", Help = "Build every accessible target", Usage = "SB build all [options]")]
    public BuildAllCommand All { get; set; } = new();

    [Cli.RegisterCmd(Name = "build", ShortName = 'b', Help = "Build the project", Usage = "SB build [options] [target ...]\nSB b [options] [target ...]\nSB build <tools|tests|benches|all> [options]")]
    public static object RegisterCommand() => new BuildCommand();

    protected override string CommandName => "build";

    protected override IEnumerable<Target> SelectRootTargets(BuildInstance instance) =>
        instance.AllTargets.Values.Where(TargetPositiveBuild);

    [Cli.RestOptions(Help = "Target names to build. If omitted, builds all PositiveBuild targets.", AllowMixed = true)]
    public string[] TargetNames { get; set; } = [];

    protected override IEnumerable<string> ExplicitRootTargetNames => TargetNames;
}

public abstract class BuildCommandBase : CommandBase
{
    [Cli.Option(Name = "build-dir", ShortName = 'o', Help = "Set build directory")]
    public string BuildDir { get; set; } = "";

    protected abstract string CommandName { get; }
    protected virtual IEnumerable<string> ExplicitRootTargetNames => [];
    protected abstract IEnumerable<Target> SelectRootTargets(BuildInstance instance);

    public override int OnExecute()
    {
        using var buildLock = ProjectBuildLock.Acquire();

        var Instance = CreateBuildInstance();
        var Toolchain = GetToolchain(Instance);
        AddBuildEmitters(Instance, Toolchain);

        var RootTargets = ResolveRootTargetNames(Instance);
        if (RootTargets.Length == 0)
            return 0;
        var BuildResult = Instance.RunBuildTargets(RootTargets);
        if (BuildResult != 0)
            return BuildResult;

        return 0;
    }

    private string[] ResolveRootTargetNames(BuildInstance Instance)
    {
        var explicitTargets = ExplicitRootTargetNames
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (explicitTargets.Length > 0)
        {
            Log.Information("Build {CommandName} selected {Count} explicit target(s).", CommandName, explicitTargets.Length);
            return explicitTargets;
        }

        var rootTargets = ApplyRootTagFilters(SelectRootTargets(Instance))
            .OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
            .Select(target => target.Name)
            .ToArray();

        if (rootTargets.Length == 0)
        {
            Log.Warning("No root targets selected for build {CommandName}.", CommandName);
        }
        else
        {
            Log.Information("Build {CommandName} selected {Count} root target(s).", CommandName, rootTargets.Length);
        }

        return rootTargets;
    }

    private sealed class ProjectBuildLock : IDisposable
    {
        private const int RetryDelayMs = 200;
        private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(10);

        private readonly string _lockPath;
        private readonly FileStream _stream;

        private ProjectBuildLock(string lockPath, FileStream stream)
        {
            _lockPath = lockPath;
            _stream = stream;
        }

        public static ProjectBuildLock Acquire()
        {
            var projectRoot = FindProjectRoot();
            var lockDir = Path.Combine(projectRoot, "build", ".sb");
            Directory.CreateDirectory(lockDir);

            var lockPath = Path.Combine(lockDir, "sb-build.lock");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var nextLogAt = TimeSpan.Zero;
            var waited = false;

            while (true)
            {
                try
                {
                    var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    WriteOwnerInfo(stream, projectRoot);
                    if (waited)
                    {
                        Log.Information("Acquired SB build lock after {Seconds:F1}s: {LockPath}", stopwatch.Elapsed.TotalSeconds, lockPath);
                    }
                    return new ProjectBuildLock(lockPath, stream);
                }
                catch (IOException)
                {
                    waited = true;
                    LogWaiting(lockPath, stopwatch.Elapsed, ref nextLogAt);
                    Thread.Sleep(RetryDelayMs);
                }
                catch (UnauthorizedAccessException)
                {
                    waited = true;
                    LogWaiting(lockPath, stopwatch.Elapsed, ref nextLogAt);
                    Thread.Sleep(RetryDelayMs);
                }
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
            Log.Verbose("Released SB build lock: {LockPath}", _lockPath);
        }

        private static string FindProjectRoot()
        {
            var current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Tools", "SB", "SB.csproj")) &&
                    File.Exists(Path.Combine(current.FullName, "Tools", "SB", "run_build.cs")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("Unable to locate SB project root from the current directory.");
        }

        private static void LogWaiting(string lockPath, TimeSpan elapsed, ref TimeSpan nextLogAt)
        {
            if (elapsed < nextLogAt)
                return;

            if (nextLogAt == TimeSpan.Zero)
            {
                Log.Information("Waiting for SB build lock: {LockPath}", lockPath);
            }
            else
            {
                Log.Information("Still waiting for SB build lock after {Seconds:F1}s: {LockPath}", elapsed.TotalSeconds, lockPath);
            }
            nextLogAt = elapsed + LogInterval;
        }

        private static void WriteOwnerInfo(FileStream stream, string projectRoot)
        {
            var commandLine = Environment.CommandLine.Replace(Environment.NewLine, " ");
            var content =
                $"pid={Environment.ProcessId}{Environment.NewLine}" +
                $"started_utc={DateTimeOffset.UtcNow:O}{Environment.NewLine}" +
                $"project_root={projectRoot}{Environment.NewLine}" +
                $"command={commandLine}{Environment.NewLine}";
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            stream.SetLength(0);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
    }

    private void AddBuildEmitters(BuildInstance Instance, IToolchain Toolchain)
    {
        Instance.AddBeforeBuildEmitter();
        Instance.AddCppPreparationEmitters();
        Instance.AddEngineTaskEmitters(Toolchain);
        Instance.AddTaskEmitter("Build.Action", new BuildActionEmitter())
            .AddDependency("Build.BeforeBuild", DependencyModel.PerTarget)
            .AddDependency("Build.BeforeBuild", DependencyModel.ExternalTarget)
            .AddDependency("Cpp.Link", DependencyModel.ExternalTarget)
            .AddDependency("Build.Action", DependencyModel.ExternalTarget);
    }

}

public abstract class BuildSubCommandBase : BuildCommandBase
{
    [Cli.Option(Name = "target", Help = "Build a single target", IsRequired = false)]
    public string? SingleTarget { get; set; }

    protected override IEnumerable<string> ExplicitRootTargetNames =>
        string.IsNullOrWhiteSpace(SingleTarget) ? [] : [SingleTarget];
}

public sealed class BuildToolsCommand : BuildSubCommandBase
{
    public BuildToolsCommand()
    {
        ConfigurationName = Stages.LoadConfigures.ReleaseConfiguration;
    }

    protected override string CommandName => "tools";

    protected override IEnumerable<Target> SelectRootTargets(BuildInstance instance) =>
        instance.AllTargets.Values.Where(target => TargetHasTag(target, TargetTags.Tool));
}

public sealed class BuildTestsCommand : BuildSubCommandBase
{
    protected override string CommandName => "tests";

    protected override IEnumerable<Target> SelectRootTargets(BuildInstance instance) =>
        instance.AllTargets.Values.Where(RegisteredAssetHelpers.IsCppTestProgram);
}

public sealed class BuildBenchesCommand : BuildSubCommandBase
{
    protected override string CommandName => "benches";

    protected override IEnumerable<Target> SelectRootTargets(BuildInstance instance) =>
        instance.AllTargets.Values.Where(RegisteredAssetHelpers.IsCppBenchProgram);
}

public sealed class BuildAllCommand : BuildSubCommandBase
{
    protected override string CommandName => "all";

    protected override IEnumerable<Target> SelectRootTargets(BuildInstance instance) =>
        instance.AllTargets.Values.Where(target =>
            !target.HasAttribute<BuildActionAttribute>() &&
            !target.HasAttribute<DebugLaunchAttribute>());
}

public sealed class BuildActionAttribute
{
    public BuildActionAttribute(Action<Target> action)
    {
        Action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public Action<Target> Action { get; }
}

public sealed class BuildActionEmitter : TaskEmitter
{
    public override bool EnableEmitter(BuildInstance instance, Target target)
        => target.HasAttribute<BuildActionAttribute>();

    public override bool EmitTargetTask(BuildInstance instance, Target target)
        => true;

    public override IArtifact? PerTargetTask(BuildInstance instance, Target target)
    {
        var action = target.GetAttribute<BuildActionAttribute>()?.Action
            ?? throw new TaskFatalError($"Target {target.Name} has no build action.");
        action(target);
        return new PlainArtifact { IsRestored = false };
    }
}

public static partial class TargetExtensions
{
    public static T BuildAction<T>(this T @this, Action<Target> action)
        where T : Target
    {
        @this.SetAttribute(new BuildActionAttribute(action), true);
        return @this;
    }
}
