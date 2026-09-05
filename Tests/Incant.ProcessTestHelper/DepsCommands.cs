using Incant.Base.Deps;

namespace Incant.ProcessTestHelper;

/// <summary>Provides a line-based handshake for cross-process database ownership and crash tests.</summary>
internal static class DepsCommands
{
    internal static int Run(string[] arguments)
    {
        if (arguments.Length != 1)
        {
            return 2;
        }

        var database = new Database(arguments[0]);
        database.Open();
        try
        {
            database.RunIfOutdated("stable", _ => { }, null, ["old"]);
            database.RunIfOutdated("stable", _ => { }, null, ["new"]);
            database.RunIfOutdated("other", _ => { }, null, ["kept"]);
            Signal("ready");

            while (Console.ReadLine() is { } command)
            {
                switch (command)
                {
                    case "close":
                        database.Close();
                        Signal("closed");
                        break;
                    case "open":
                        database.Open();
                        Signal("opened");
                        break;
                    case "append":
                        database.RunIfOutdated("later", _ => { }, null, ["after"]);
                        Signal("appended");
                        break;
                    case "compact":
                        database.Compact();
                        Signal("compacted");
                        break;
                    case "wait-before-compact":
                        Signal("before-compact");
                        Console.ReadLine();
                        database.Compact();
                        Signal("compacted");
                        break;
                    case "quit":
                        return 0;
                    default:
                        return 2;
                }
            }

            return 0;
        }
        finally
        {
            database.Close();
        }
    }

    private static void Signal(string message)
    {
        Console.WriteLine(message);
        Console.Out.Flush();
    }
}
