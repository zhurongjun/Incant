using Cli = SB.Cli;
using SB;
using Serilog;

namespace SB;

public class VSCommand : CommandBase
{
    [Cli.Option(Name = "solution-name", Help = "Name of the solution file (without extension)", IsRequired = false)]
    public string SolutionName { get; set; } = "SakuraEngine";

    [Cli.Option(Name = "output", Help = "Output directory for solution files", IsRequired = false)]
    public string OutputDirectory { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), ".vs");

    [Cli.Option(Name = "only-positive", Help = "Only generate projects for PositiveBuild root targets", IsRequired = false)]
    public bool OnlyPositive { get; set; } = false;


    public override int OnExecute()
    {
        var Instance = CreateBuildInstance();
        var Toolchain = GetToolchain(Instance);
        var BuildDirs = Instance.GetStage<Stages.PrepareEngineDirectoriesStage>()!;
        Log.Information("Generating Visual Studio solution...");

        // Add VS emitter to generate project files
        var emitter = Instance.AddTaskEmitter("VSEmitter", new VSEmitter(Toolchain)
        {
            RootDirectory = !string.IsNullOrEmpty(BuildDirs.EngineDir) ? BuildDirs.EngineDir : Directory.GetCurrentDirectory(),
            OutputDirectory = OutputDirectory
        });

        // Run the build pipeline to process all loaded targets.
        RunBuildForAllTargets(Instance, OnlyPositive);

        // Generate solution file
        var solutionPath = Path.Combine(OutputDirectory, $"{SolutionName}.sln");
        emitter.GenerateSolution(solutionPath, SolutionName);

        Log.Information("Visual Studio solution generated at: {Path}", Path.GetFullPath(solutionPath));

        return 0;
    }

    [Cli.RegisterCmd(Name = "vs", Help = "Generate Visual Studio solution", Usage = "SB vs [options]")]
    public static object RegisterCommand() => new VSCommand();
}
