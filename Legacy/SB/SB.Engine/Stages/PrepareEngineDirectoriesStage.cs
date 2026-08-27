using Serilog;
using SB.Core;
using BS = SB.BuildInstance;
namespace SB.Stages;

public class PrepareEngineDirectoriesStage : PrepareBuildDirectoriesStage
{
    public override bool Run(BuildInstance instance)
    {
        _EngineDir = Path.GetFullPath(Path.Join(SourceLocation.Directory(), "..", "..", "..", ".."));
        _ProjectRoot = Directory.GetCurrentDirectory();

        // check exists
        if (!Directory.Exists(EngineDir))
            throw new DirectoryNotFoundException($"EngineDir '{EngineDir}' not found");
        if (!Directory.Exists(ProjectRoot))
            throw new DirectoryNotFoundException($"ProjectRoot '{ProjectRoot}' not found");

        Log.Information("EngineDir: {EngineDir}", EngineDir);
        Log.Information("ProjectRoot: {ProjectRoot}", ProjectRoot);
        return base.Run(instance);
    }

    public override string GetProjectRoot() => _ProjectRoot!;

    private string? _EngineDir;
    private string? _ProjectRoot;
    public string EngineDir => _EngineDir ?? throw new InvalidOperationException("BuildPath.EngineDir is not setup");
    public string ProjectRoot => _ProjectRoot ?? throw new InvalidOperationException("BuildPath.ProjectRoot is not setup");
}
