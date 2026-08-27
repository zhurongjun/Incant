using Cli = SB.Cli;
namespace SB;

using Serilog;
using XCode;

public class XCodeCommand : CommandBase
{
    [Cli.Option(Name = "xcodeproj-name", Help = "Name of the xcode project file (without extension)", IsRequired = false)]
    public string XCodeProjName { get; set; } = "SakuraEngine";

    [Cli.Option(Name = "output", Help = "Output directory for project files", IsRequired = false)]
    public string OutputDirectory { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), ".xcode");

    [Cli.Option(Name = "only-positive", Help = "Only generate projects for PositiveBuild root targets", IsRequired = false)]
    public bool OnlyPositive { get; set; } = false;

    public override int OnExecute()
    {
        var Instance = CreateBuildInstance();
        var Toolchain = GetToolchain(Instance);
        var BuildDirs = Instance.GetStage<Stages.PrepareEngineDirectoriesStage>()!;
        Log.Information("Generating XCode project...");

        XCodeEmitter emitter = new(Toolchain, OutputDirectory, XCodeProjName, BuildDirs.ProjectRoot, ConfigurationName.ToLowerInvariant());
        Instance.AddTaskEmitter("XCodeEmitter", emitter);
        RunBuildForAllTargets(Instance, OnlyPositive);
        XCodeEmitter.GenerateProjectFile(emitter.ProjectInfo);

        Log.Information("XCode project generated at: {Path}", Path.GetFullPath(Path.Combine(OutputDirectory, $"{XCodeProjName}.xcodeproj")));
        return 0;
    }

    [Cli.RegisterCmd(Name = "xcode", Help = "Generate XCode project", Usage = "SB xcode [options]")]
    public static object RegisterCommand() => new XCodeCommand();
}
