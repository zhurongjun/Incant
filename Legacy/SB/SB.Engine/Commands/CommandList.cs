using Cli = SB.Cli;
using SB.Core;

namespace SB;

public class ListCommand
{
    [Cli.RegisterCmd(Name = "list", Help = "List SB project information", Usage = "SB list [sub-commands] [options]")]
    public static object RegisterCommand() => new ListCommand();

    [Cli.SubCmd(Name = "packages", Help = "List package information", Usage = "SB list packages [options]")]
    public ListPackageCommand Packages { get; set; } = new();

    [Cli.SubCmd(Name = "targets", Help = "List target information", Usage = "SB list targets [options]")]
    public ListTargetCommand Targets { get; set; } = new();

    [Cli.SubCmd(Name = "setups", Help = "List setup information", Usage = "SB list setups [options]")]
    public ListSetupCommand Setups { get; set; } = new();

    [Cli.SubCmd(Name = "tags", Help = "List target tags", Usage = "SB list tags [options]")]
    public ListTagsCommand Tags { get; set; } = new();

    [Cli.SubCmd(Name = "tests", Help = "List test targets", Usage = "SB list tests [options]")]
    public ListTestsCommand Tests { get; set; } = new();

    [Cli.SubCmd(Name = "benches", Help = "List benchmark targets", Usage = "SB list benches [options]")]
    public ListBenchesCommand Benches { get; set; } = new();
}

public abstract class ListCommandBase : CommandBase
{
    protected BuildInstance CreateListBuildInstance() => CreateBuildInstance();

    // Present source locations relative to the current SB invocation directory.
    protected static string ToDisplayPath(string path) =>
        Path.GetRelativePath(Directory.GetCurrentDirectory(), path).Replace('\\', '/');

    protected override bool DumpCounters => false;

    // Shared Cli.Writer fragments keep the three list subcommands visually aligned.
    protected static void WriteHeader(Cli.Writer writer, string label, int count)
    {
        writer
            .StyleBold()
            .StyleFrontCyan()
            .Write(label)
            .StyleClear()
            .Write(": ")
            .StyleFrontYellow()
            .Write(count.ToString())
            .StyleClear()
            .NextLine();
    }

    protected static void WriteName(Cli.Writer writer, string name)
    {
        writer
            .StyleFrontGreen()
            .Write(name)
            .StyleClear();
    }

    protected static void WriteKey(Cli.Writer writer, string key)
    {
        writer
            .StyleFrontMagenta()
            .Write(key)
            .StyleClear();
    }

    protected static string FormatTargetTags(Target target)
    {
        var tags = target.Tags()
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return tags.Length == 0 ? "none" : string.Join(", ", tags);
    }
}

public class ListPackageCommand : ListCommandBase
{
    public override int OnExecute()
    {
        // Build the package registry by loading all build scripts, then sort it
        // into stable output order for inspection.
        var instance = CreateListBuildInstance();
        var packages = instance.Packages.Values
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Cli.Writer writer = new();

        // Emit each package with its supported versions and package target installers.
        WriteHeader(writer, "Packages", packages.Length);
        foreach (var package in packages)
        {
            var versions = package.Versions.Count == 0
                ? "any"
                : string.Join(", ", package.Versions.OrderBy(version => version).Select(version => version.ToString()));

            WriteName(writer, package.Name);
            writer.Write("  ");
            WriteKey(writer, "versions");
            writer.Write(": ").StyleFrontYellow().Write(versions).StyleClear().NextLine();
            foreach (var target in package.Targets.OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase))
            {
                writer.Write("  ");
                WriteKey(writer, "target");
                writer.Write(": ").StyleFrontGreen().Write(target.Name).StyleClear();
                writer.Write(" (").StyleFrontCyan().Write(target.TargetType.Name).StyleClear().Write(")").NextLine();
                writer.Write("    ");
                WriteKey(writer, "at");
                writer.Write(": ").StyleFrontWhite().Write($"{ToDisplayPath(target.Location)}:{target.LineNumber}").StyleClear().NextLine();
            }
        }

        writer.Dump();
        return 0;
    }
}

public class ListTargetCommand : ListCommandBase
{
    public override int OnExecute()
    {
        // Build the target registry by loading target scripts and their tags.
        var instance = CreateListBuildInstance();
        var targets = instance.AllTargets.Values
            .OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Cli.Writer writer = new();

        // Emit target identity, tags, source location, and direct dependencies.
        WriteHeader(writer, "Targets", targets.Length);
        foreach (var target in targets)
        {
            var targetType = target.GetTargetType()?.ToString() ?? "Unset";

            WriteName(writer, target.Name);
            writer.Write("  ");
            WriteKey(writer, "type");
            writer.Write(": ").StyleFrontYellow().Write(targetType).StyleClear();
            writer.Write("  ");
            WriteKey(writer, "tags");
            writer.Write(": ").StyleFrontMagenta().Write(FormatTargetTags(target)).StyleClear().NextLine();

            writer.Write("  ");
            WriteKey(writer, "kind");
            writer.Write(": ").StyleFrontCyan().Write(target.GetType().Name).StyleClear();
            writer.Write("  ");
            WriteKey(writer, "positive");
            writer.Write(": ").StyleFrontYellow().Write(TargetPositiveBuild(target).ToString()).StyleClear().NextLine();

            writer.Write("  ");
            WriteKey(writer, "package");
            writer.Write(": ").StyleFrontYellow().Write(target.IsFromPackage.ToString()).StyleClear().NextLine();

            writer.Write("  ");
            WriteKey(writer, "at");
            writer.Write(": ").StyleFrontWhite().Write($"{ToDisplayPath(target.Location)}:{target.LineNumber}").StyleClear().NextLine();
            WriteDependencySet(writer, "public", target.PublicDependencies);
            WriteDependencySet(writer, "private", target.PrivateDependencies);
            WriteDependencySet(writer, "interface", target.InterfaceDependencies);
        }

        writer.Dump();
        return 0;
    }

    private static void WriteDependencySet(Cli.Writer writer, string name, IReadOnlySet<string> dependencies)
    {
        if (dependencies.Count == 0)
            return;

        writer.Write("  ");
        WriteKey(writer, $"{name} deps");
        writer.Write(": ")
            .StyleFrontWhite()
            .Write(string.Join(", ", dependencies.OrderBy(dep => dep, StringComparer.OrdinalIgnoreCase)))
            .StyleClear()
            .NextLine();
    }
}

public class ListTagsCommand : ListCommandBase
{
    public override int OnExecute()
    {
        // Aggregate the open string tag set from the loaded targets themselves.
        var instance = CreateListBuildInstance();
        var tags = instance.AllTargets.Values
            .SelectMany(target => target.Tags().Select(tag => (Tag: tag, Target: target)))
            .GroupBy(entry => entry.Tag, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Cli.Writer writer = new();

        WriteHeader(writer, "Tags", tags.Length);
        foreach (var tag in tags)
        {
            var targets = tag
                .Select(entry => entry.Target.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            WriteName(writer, tag.Key);
            writer.Write("  ");
            WriteKey(writer, "targets");
            writer.Write(": ").StyleFrontYellow().Write(targets.Length.ToString()).StyleClear().NextLine();
            writer.Write("  ");
            WriteKey(writer, "names");
            writer.Write(": ")
                .StyleFrontWhite()
                .Write(string.Join(", ", targets))
                .StyleClear()
                .NextLine();
        }

        writer.Dump();
        return 0;
    }
}

public class ListTestsCommand : ListCommandBase
{
    [Cli.Option(Name = "module", Help = "Limit results to a module name or module path", IsRequired = false)]
    public string Module { get; set; } = "";

    [Cli.Option(Name = "filter", Help = "Filter results by regex or text", IsRequired = false)]
    public string Filter { get; set; } = "";

    public override int OnExecute()
    {
        var instance = CreateListBuildInstance();
        var tests = RegisteredTestModuleExecutionPlan.FromTargets(instance.AllTargets.Values, Module, Filter).Tests;
        Cli.Writer writer = new();

        WriteHeader(writer, "Tests", tests.Count);
        foreach (var test in tests)
        {
            WriteName(writer, test.ModuleName);
            writer.Write(": ");
            writer.StyleFrontGreen().Write(test.TestName).StyleClear().NextLine();

            writer.Write("  ");
            WriteKey(writer, "target");
            writer.Write(": ").StyleFrontYellow().Write(test.TargetName).StyleClear().NextLine();

            writer.Write("  ");
            WriteKey(writer, "directory");
            writer.Write(": ").StyleFrontWhite().Write(RegisteredAssetHelpers.ToDisplayPath(test.ModuleDirectory)).StyleClear().NextLine();
        }

        writer.Dump();
        return 0;
    }
}

public class ListBenchesCommand : ListCommandBase
{
    [Cli.Option(Name = "module", Help = "Limit results to a module name or module path", IsRequired = false)]
    public string Module { get; set; } = "";

    [Cli.Option(Name = "filter", Help = "Filter results by regex or text", IsRequired = false)]
    public string Filter { get; set; } = "";

    public override int OnExecute()
    {
        var instance = CreateListBuildInstance();
        var benches = RegisteredBenchModuleExecutionPlan.FromTargets(instance.AllTargets.Values, Module, Filter).Benches;
        Cli.Writer writer = new();

        WriteHeader(writer, "Benches", benches.Count);
        foreach (var bench in benches)
        {
            WriteName(writer, bench.ModuleName);
            writer.Write(": ");
            writer.StyleFrontGreen().Write(bench.BenchName).StyleClear().NextLine();

            writer.Write("  ");
            WriteKey(writer, "target");
            writer.Write(": ").StyleFrontYellow().Write(bench.TargetName).StyleClear().NextLine();

            writer.Write("  ");
            WriteKey(writer, "directory");
            writer.Write(": ").StyleFrontWhite().Write(RegisteredAssetHelpers.ToDisplayPath(bench.ModuleDirectory)).StyleClear().NextLine();
        }

        writer.Dump();
        return 0;
    }
}

public class ListSetupCommand : ListCommandBase
{
    public override int OnExecute()
    {
        // Register the common emitter families so setup entries contributed by
        // toolchains and C++ build emitters are visible.
        var instance = CreateListBuildInstance();
        var toolchain = GetToolchain(instance);
        instance.AddCppPreparationEmitters();
        instance.AddEngineTaskEmitters(toolchain);

        var setups = instance.Setups
            .OrderBy(setup => setup.GetType().FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Cli.Writer writer = new();

        // Emit setup type, whether it has already run during initialization, and
        // the assembly that contributed it.
        WriteHeader(writer, "Setups", setups.Length);
        foreach (var setup in setups)
        {
            var setupType = setup.GetType();
            var status = instance.CompletedSetupTypes.Contains(setupType) ? "completed" : "pending";
            var registration = instance.GetSetupRegistration(setup);
            WriteName(writer, setupType.FullName ?? setupType.Name);
            writer.Write("  ");
            WriteKey(writer, "status");
            writer.Write(": ");
            if (status == "completed")
            {
                writer.StyleFrontGreen();
            }
            else
            {
                writer.StyleFrontYellow();
            }
            writer.Write(status).StyleClear();
            writer.Write("  ");
            WriteKey(writer, "assembly");
            writer.Write(": ").StyleFrontCyan().Write(setupType.Assembly.GetName().Name ?? "").StyleClear().NextLine();
            WriteSetupRegistration(writer, registration);
        }

        writer.Dump();
        return 0;
    }

    private static void WriteSetupRegistration(Cli.Writer writer, SetupRegistrationInfo? registration)
    {
        writer.Write("  ");
        WriteKey(writer, "registered");
        writer.Write(": ");
        if (registration is null)
        {
            writer.StyleFrontYellow().Write("unknown").StyleClear().NextLine();
            return;
        }

        if (!string.IsNullOrWhiteSpace(registration.Location))
        {
            writer.StyleFrontWhite()
                .Write($"{ToDisplayPath(registration.Location)}:{registration.LineNumber}")
                .StyleClear()
                .NextLine();
            return;
        }

        writer.StyleFrontYellow().Write("unknown").StyleClear().NextLine();
    }
}
