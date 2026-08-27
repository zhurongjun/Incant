using Cli = SB.Cli;
using SB;
using Serilog;

namespace SB;

public class VSCodeCommand : CommandBase
{
    [Cli.Option(Name = "debugger", Help = "debugger to use", IsRequired = false, Selections = ["cppdbg", "lldb-dap", "codelldb", "cppvsdbg"])]
    public string Debugger { get; set; } = "";

    [Cli.Option(Name = "workspace", Help = "Workspace root directory", IsRequired = false)]
    public string? WorkspaceRoot { get; set; } 

    [Cli.Option(Name = "preserve-user", Help = "Preserve user-created debug configurations", IsRequired = false)]
    public bool PreserveUser { get; set; } = true;

    [Cli.Option(Name = "clear", Help = "Preserve user-created debug configurations", IsRequired = false)]
    public bool ClearMode { get; set; } = false;

    [Cli.Option(Name = "filter", Help = "Filter targets to generate configurations for (use regex)", IsRequired = false)]
    public string Filter { get; set; } = "";

    [Cli.Option(Name = "only-positive", Help = "Only generate configurations for PositiveBuild root targets", IsRequired = false)]
    public bool OnlyPositive { get; set; } = false;

    private static string GetTempFilesDir(BuildInstance instance)
        => Path.Combine(instance.GetStage<Stages.PrepareEngineDirectoriesStage>()!.TempDir, "vscode");

    public override int OnExecute()
    {
        var instance = CreateBuildInstance();
        var toolchain = GetToolchain(instance);
        Log.Information("Generating VSCode debug configurations...");

        var buildDirs = instance.GetStage<Stages.PrepareEngineDirectoriesStage>()!;
        var tempFilesDir = GetTempFilesDir(instance);
        if (ClearMode)
        {
            var emitter = new VSCodeDebugEmitter();
            emitter.WorkspaceRoot = !string.IsNullOrEmpty(WorkspaceRoot) ? WorkspaceRoot :
                                    (!string.IsNullOrEmpty(buildDirs.EngineDir) ? buildDirs.EngineDir : Directory.GetCurrentDirectory());
            emitter.CmdFilesOutputDir = Path.Combine(tempFilesDir, "task_cmds");
            emitter.MergedNatvisOutputDir = Path.Combine(tempFilesDir, "natvis");
            // emitter.Debugger = Debugger.ToLower();
            emitter.ConfigurationName = ConfigurationName;
            emitter.Toolchain = toolchain;
            emitter.PreserveUserConfig = PreserveUser;
            emitter.FilterRegex = Filter;
            emitter.Clear();
            Log.Information("Cleared generated VSCode debug configurations in: {Path}", Path.GetFullPath(Path.Combine(emitter.WorkspaceRoot, ".vscode")));
        }
        else
        {
            // Add VSCode emitter
            var emitter = new VSCodeDebugEmitter();
            emitter.WorkspaceRoot = !string.IsNullOrEmpty(WorkspaceRoot) ? WorkspaceRoot :
                                    (!string.IsNullOrEmpty(buildDirs.EngineDir) ? buildDirs.EngineDir : Directory.GetCurrentDirectory());
            emitter.CmdFilesOutputDir = Path.Combine(tempFilesDir, "task_cmds");
            emitter.MergedNatvisOutputDir = Path.Combine(tempFilesDir, "natvis");
            emitter.Debugger = Debugger;
            emitter.ConfigurationName = ConfigurationName;
            emitter.Toolchain = toolchain;
            emitter.PreserveUserConfig = PreserveUser;
            emitter.FilterRegex = Filter;
            instance.AddTaskEmitter("VSCodeDebugEmitter", emitter);

            // Run the build pipeline to process all loaded targets.
            RunBuildForAllTargets(instance, OnlyPositive);

            // Generate the debug configurations
            emitter.Generate();
            Log.Information("VSCode debug configurations generated in: {Path}", Path.GetFullPath(Path.Combine(emitter.WorkspaceRoot, ".vscode")));
        }

        return 0;
    }

    [Cli.RegisterCmd(Name = "vscode", Help = "Generate VSCode debug configurations", Usage = "SB vscode [options]")]
    public static object RegisterCommand() => new VSCodeCommand();
}
