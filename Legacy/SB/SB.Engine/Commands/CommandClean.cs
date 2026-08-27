using Cli = SB.Cli;
using SB;
using Serilog;

namespace SB;

public class CleanCommand : CommandBase
{
    [Cli.Option(Name = "database", ShortName = 'd', Help = "Database to clean", IsRequired = false, Selections = ["all", "packages", "targets", "misc", "sdks"])]
    public string Database { get; set; } = "targets";

    public override int OnExecute()
    {
        var Instance = CreateBuildInstance();
        Log.Information("Cleaning build cache dependency databases for {Database}...", Database);

        // Clean dependency databases using API
        try
        {
            var buildDatabases = Instance.GetStage<Stages.PrepareEngineDatabasesStage>()!;
            bool all = Database == "all";
            if (all || Database == "targets")
                buildDatabases.GetCompileDatabase(false).ClearDatabase();
            if (all || Database == "packages")
                buildDatabases.GetCompileDatabase(true).ClearDatabase();
            if (all || Database == "misc")
                buildDatabases.Misc.ClearDatabase();
            if (all || Database == "sdks")
            {
                buildDatabases.Download.ClearDatabase();
                buildDatabases.SDK.ClearDatabase();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to clear databases: {Error}", ex.Message);
        }
        return 0;
    }

    [Cli.RegisterCmd(Name = "clean", ShortName = 'c', Help = "Clean build cache and dependency databases", Usage = "SB clean [options]\nSB c [options]")]
    public static object RegisterCommand() => new CleanCommand();
}
