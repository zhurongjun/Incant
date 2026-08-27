using Cli = SB.Cli;
using SB;
using Serilog;

namespace SB;

public class CompileCommandsCommand : CommandBase
{
    [Cli.RegisterCmd(Name = "compile_commands", Help = "Generate Compile Commands for IDEs", Usage = "SB compile_commands [options]")]
    public static object RegisterCommand() => new CompileCommandsCommand();

    [Cli.Option(Name = "clean-clangd-cache", Help = "Clean up clangd cache after generating compile commands", Selections = ["none", "cpp", "all"])]
    public string CleanClangdCache { get; set; } = "none";

    [Cli.Option(Name = "only-positive", Help = "Only generate compile commands for PositiveBuild root targets", IsRequired = false)]
    public bool OnlyPositive { get; set; } = false;

    public override int OnExecute()
    {
        var Instance = CreateBuildInstance();
        var Toolchain = GetToolchain(Instance);
        var BuildDirs = Instance.GetStage<Stages.PrepareEngineDirectoriesStage>()!;
        
        Instance.AddCompileCommandsEmitter(Toolchain);
        RunBuildForAllTargets(Instance, OnlyPositive);

        // solve paths
        var outDir = Path.Join(BuildDirs.TempDir, "compile_commands");
        var cppOutDir = Path.Join(outDir, "cpp");

        // output compile commands
        Directory.CreateDirectory(cppOutDir);
        (Instance.GetTaskEmitter("Cpp.CompileCommands") as CompileCommandsEmitter)!
            .WriteToFile(Path.Join(cppOutDir, "compile_commands.json"));

        // clean up clangd cache if needed
        if (CleanClangdCache != "none")
        {
            bool cleanCpp = CleanClangdCache == "cpp" || CleanClangdCache == "all";
            var cppClangdCacheDir = Path.Join(cppOutDir, ".cache");

            if (cleanCpp && Directory.Exists(cppClangdCacheDir))
            {
                Directory.Delete(cppClangdCacheDir, true);
                Log.Information("Cleaned up clangd cache for C++ at {Dir}", cppClangdCacheDir);
            }
        }

        return 0;
    }

}
