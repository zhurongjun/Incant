using SB;
using SB.Core;
using Serilog;

BuildTrace.Mark("run_build.enter");

// load all assemblies for commands
BuildTrace.Measure("run_build.load_assemblies", () =>
{
    AppDomain.CurrentDomain.Load("SB.Core");
    AppDomain.CurrentDomain.Load("SB.Engine");
});

// filter SB args passed by dotnet run
string[] filteredArgs = args;
if (args.Length > 0 && args[0] == "SB")
{
    filteredArgs = args[1..];
}
BuildTrace.Mark("run_build.args", $"args={args.Length} filtered={filteredArgs.Length}");

// now, invoke command
SB.Cli.Command cmd = new SB.Cli.Command
{
    Name = "SB",
    Help = "Sakura Build System (SB) - A fast, modern build system for C++ projects",
    Usage = "SB [sub-commands] [options]"
};
var banner =
@"

    _____         _                        ____          _  _      _ 
   / ____|       | |                      |  _ \        (_)| |    | |
  | (___    __ _ | | __ _   _  _ __  __ _ | |_) | _   _  _ | |  __| |
   \___ \  / _` || |/ /| | | || '__|/ _` ||  _ < | | | || || | / _` |
   ____) || (_| ||   < | |_| || |  | (_| || |_) || |_| || || || (_| |
  |_____/  \__,_||_|\_\ \__,_||_|   \__,_||____/  \__,_||_||_| \__,_|



";
using (BuildTrace.Scope("run_build.invoke_cli"))
{
    return SB.Cli.ReflCommand.InvokeDefaultCommandFromDomain(AppDomain.CurrentDomain, cmd, filteredArgs, banner);
}
