
using System.Runtime.InteropServices.Marshalling;
using SB.Core;
using Serilog;

namespace SB.Stages
{

public class PrepareBuildDirectoriesStage : IBuildStage
{
    protected BuildInstance Owner { get; private set; } = null!;

    public virtual bool Run(BuildInstance instance)
    {
        Owner = instance;
        var toolchain = instance.GetStage<Stages.ChooseToolchain>()!.Toolchain!;
        var projectRoot = GetProjectRoot();
        _CachedTempDir = Directory.CreateDirectory(Path.Combine(projectRoot, "build/.sb")).FullName;
        _CachedDownloadDir = Directory.CreateDirectory(Path.Combine(_CachedTempDir, "downloads")).FullName;
        _CachedToolDir = Directory.CreateDirectory(Path.Combine(_CachedTempDir, "tools")).FullName;
        _CachedDBDir = Directory.CreateDirectory(Path.Combine(_CachedTempDir, "dbs")).FullName;
        _CachedBuildDir = Directory.CreateDirectory(Path.Combine(projectRoot, "build/.build", toolchain.Name)).FullName;
        _CachedPackageBuildDir = Directory.CreateDirectory(Path.Combine(projectRoot, "build/.pkgs", toolchain.Name)).FullName;
        return true;
    }

    public virtual string GetProjectRoot() => Directory.GetCurrentDirectory();

    // paths cache
    private string? _CachedTempDir;
    private string? _CachedBuildDir;
    private string? _CachedPackageBuildDir;
    private string? _CachedDownloadDir;
    private string? _CachedToolDir;
    private string? _CachedDBDir;

    // getter
    public string TempDir => _CachedTempDir ?? throw new InvalidOperationException("BuildPath.TempDir is not setup");
    public string BuildDir => _CachedBuildDir ?? throw new InvalidOperationException("BuildPath.BuildDir is not setup");
    public string PackageBuildDir => _CachedPackageBuildDir ?? throw new InvalidOperationException("BuildPath.PackageBuildDir is not setup");
    public string DownloadDir => _CachedDownloadDir ?? throw new InvalidOperationException("BuildPath.DownloadDir is not setup");
    public string ToolDir => _CachedToolDir ?? throw new InvalidOperationException("BuildPath.ToolDir is not setup");
    public string DBDir => _CachedDBDir ?? throw new InvalidOperationException("BuildPath.DBDir is not setup");

    // store dirs
    public string StoreDirDeps => ".deps";
    public string StoreDirObjs => ".objs";
    public string StoreDirGens => ".gens";

    // some helpers
    public string SolveUniqueBuildDir(string? configurationName = null)
    {
        var configures = Owner.GetStage<Stages.LoadConfigures>()!;
        configurationName ??= configures.ConfigurationName;
        return $"{Owner.TargetOS}-{Owner.TargetArch}-{configurationName}";
    }
    public string GetBuildDirForTarget(Target target)
    {
        return target.IsFromPackage ? PackageBuildDir : BuildDir;
    }
    public string GetStoreDir(string buildDir, string storeDir, string? configurationName = null)
    {
        string uniqueDir = SolveUniqueBuildDir(configurationName);
        string fullPath = Path.Combine(buildDir, storeDir, uniqueDir);
        return Directory.CreateDirectory(fullPath).FullName;
    }
}

}

namespace SB
{

public static class TargetBuildPathExtensions
{
    public static string GetBuildDir(this Target target)
    {
        var buildDirs = target.Instance.GetStage<Stages.PrepareBuildDirectoriesStage>()!;
        return buildDirs.GetBuildDirForTarget(target);
    }

    public static string GetStoreDir(this Target target, string storeKind, string? configurationName = null)
    {
        var buildDirs = target.Instance.GetStage<Stages.PrepareBuildDirectoriesStage>()!;
        string buildDir = GetBuildDir(target);
        string uniqueDir = buildDirs.SolveUniqueBuildDir(configurationName);
        string fullPath = Path.Combine(buildDir, storeKind, uniqueDir, target.Name);
        return Directory.CreateDirectory(fullPath).FullName;
    }

    public static string GetBinaryDir(this Target target, string? configurationName = null)
    {
        var buildDirs = target.Instance.GetStage<Stages.PrepareBuildDirectoriesStage>()!;
        string buildDir = GetBuildDir(target);
        string uniqueDir = buildDirs.SolveUniqueBuildDir(configurationName);
        string fullPath = Path.Combine(buildDir, uniqueDir);
        return Directory.CreateDirectory(fullPath).FullName;
    }

    public static string GetBuildSrcDepsDir(this Target target, string? configurationName = null)
    {
        var buildDirs = target.Instance.GetStage<Stages.PrepareBuildDirectoriesStage>()!;
        return target.GetStoreDir(buildDirs.StoreDirDeps, configurationName);
    }

    public static string GetBuildObjsDir(this Target target, string? configurationName = null)
    {
        var buildDirs = target.Instance.GetStage<Stages.PrepareBuildDirectoriesStage>()!;
        return target.GetStoreDir(buildDirs.StoreDirObjs, configurationName);
    }

    public static string GetBuildGenDir(this Target target, string? configurationName = null)
    {
        var buildDirs = target.Instance.GetStage<Stages.PrepareBuildDirectoriesStage>()!;
        return target.GetStoreDir(buildDirs.StoreDirGens, configurationName);
    }
}

}
