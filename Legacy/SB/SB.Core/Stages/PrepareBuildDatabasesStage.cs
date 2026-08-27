using SB.Core;

namespace SB.Stages;

public class PrepareBuildDatabasesStage : IBuildStage
{
    public PrepareBuildDatabasesStage(bool defaultUseSHA = false)
    {
        _defaultUseSHA = defaultUseSHA;
    }

    // depends
    private DependencyDatabase? _PkgCompile;
    private DependencyDatabase? _TargetCompile;

    public virtual bool Run(BuildInstance instance)
    {
        var buildDirs = instance.GetStage<Stages.PrepareBuildDirectoriesStage>()!;
        var configures = instance.GetStage<Stages.LoadConfigures>()!;
        _PkgCompile ??= new DependencyDatabase(buildDirs.PackageBuildDir, $"CppCompile.Paks.{configures.ConfigurationName}", instance.DependencyDatabaseCache, _defaultUseSHA);
        _TargetCompile ??= new DependencyDatabase(buildDirs.BuildDir, $"CppCompile.Targets.{configures.ConfigurationName}", instance.DependencyDatabaseCache, _defaultUseSHA);
        return true;
    }

    // getters
    public DependencyDatabase PkgCompile => _PkgCompile ?? throw new InvalidOperationException("BuildDepends not initialized");
    public DependencyDatabase TargetCompile => _TargetCompile ?? throw new InvalidOperationException("BuildDepends not initialized");

    // solve helper
    public DependencyDatabase GetCompileDatabase(bool isPackageTarget) => isPackageTarget ? PkgCompile : TargetCompile;
    public DependencyDatabase GetCompileDatabaseForTarget(Target target) => GetCompileDatabase(target.IsFromPackage);

    protected readonly bool _defaultUseSHA;
}
