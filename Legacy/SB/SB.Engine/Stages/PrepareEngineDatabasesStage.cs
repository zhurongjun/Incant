using SB.Core;

namespace SB.Stages;

public class PrepareEngineDatabasesStage : PrepareBuildDatabasesStage
{
    public PrepareEngineDatabasesStage(bool defaultUseSHA = false)
        : base(defaultUseSHA)
    {
    }

    private DependencyDatabase? _Download;
    private DependencyDatabase? _SDK;
    private DependencyDatabase? _Misc;

    public override bool Run(BuildInstance instance)
    {
        var buildDirs = instance.GetStage<Stages.PrepareEngineDirectoriesStage>()!;
        var configures = instance.GetStage<Stages.LoadConfigures>()!;
        _Download ??= new DependencyDatabase(buildDirs.TempDir, "Engine.Downloads", instance.DependencyDatabaseCache, _defaultUseSHA);
        _SDK ??= new DependencyDatabase(buildDirs.TempDir, $"Engine.SDKs.{configures.ConfigurationName}", instance.DependencyDatabaseCache, _defaultUseSHA);
        _Misc ??= new DependencyDatabase(buildDirs.TempDir, "Engine.Misc", instance.DependencyDatabaseCache, _defaultUseSHA);
        return base.Run(instance);
    }

    public DependencyDatabase Download => _Download ?? throw new InvalidOperationException("InstallDepends not initialized");
    public DependencyDatabase SDK => _SDK ?? throw new InvalidOperationException("InstallDepends not initialized");
    public DependencyDatabase Misc => _Misc ?? throw new InvalidOperationException("EngineDepends not initialized");
}
