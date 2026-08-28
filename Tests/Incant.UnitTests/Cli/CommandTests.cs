using Incant.Base.Cli;

namespace Incant.UnitTests.Cli;

public sealed class CommandTests
{
    [Fact]
    public void FindOptionUsesFullNameOrSingleCharacterShortName()
    {
        StringOption fullNameMatch = CreateStringOption("mode");
        var shortNameMatch = new UIntOption
        {
            Name = "model",
            ShortName = 'm',
            Help = "Matched by short name."
        };
        Command command = CreateCommand(options: [fullNameMatch, shortNameMatch]);

        Assert.Same(fullNameMatch, command.FindOption("mode"));
        Assert.Same(shortNameMatch, command.FindOption('m'));
        Assert.Same(shortNameMatch, command.FindOption("m"));
        Assert.Same(fullNameMatch, command.FindOption<StringOption>("mode"));
        Assert.Same(shortNameMatch, command.FindOption<UIntOption>('m'));
        Assert.Same(shortNameMatch, command.FindOption<UIntOption>("m"));
        Assert.Null(command.FindOption<UIntOption>("mode"));
        Assert.Null(command.FindOption("missing"));
    }

    [Fact]
    public void FindOptionUsesOrdinalCaseSensitiveNames()
    {
        StringOption option = CreateStringOption("mode", 'm');
        Command command = CreateCommand(options: [option]);

        Assert.Same(option, command.FindOption("mode"));
        Assert.Null(command.FindOption("Mode"));
        Assert.Same(option, command.FindOption('m'));
        Assert.Null(command.FindOption('M'));
    }

    [Fact]
    public void FindOptionRejectsNullName()
    {
        Command command = CreateCommand();

        Assert.Throws<ArgumentNullException>(() => command.FindOption(null!));
        Assert.Throws<ArgumentNullException>(() => command.FindOption<StringOption>(null!));
    }

    [Fact]
    public void AddOptionAppendsOption()
    {
        Command command = CreateCommand();
        StringOption option = CreateStringOption("mode");

        command.AddOption(option);

        Assert.Same(option, Assert.Single(command.Options));
        Assert.Same(option, command.FindOption("mode"));
    }

    [Fact]
    public void OptionMutatorsRejectNullInstances()
    {
        Command command = CreateCommand();

        Assert.Throws<ArgumentNullException>(() => command.AddOption(null!));
        Assert.Throws<ArgumentNullException>(() => command.RemoveOption((IOption)null!));
        Assert.Throws<ArgumentNullException>(() => command.RemoveOption((string)null!));
        Assert.Throws<ArgumentNullException>(() => command.SetRestOption(null!));
    }

    [Fact]
    public void OptionInitializerRejectsNullCollectionOrElement()
    {
        Assert.Throws<ArgumentNullException>(() => _ = new Command
        {
            Name = "incant",
            Help = "Build a project.",
            Usage = "incant [options]",
            Options = null!
        });
        Assert.Throws<ArgumentNullException>(() => CreateCommand(options: [null!]));
    }

    [Fact]
    public void OptionInitializerCopiesTheConfiguredCollection()
    {
        StringOption option = CreateStringOption("mode");
        List<IOption> configuredOptions = [option];
        Command command = CreateCommand(options: configuredOptions);

        configuredOptions.Clear();

        Assert.Same(option, Assert.Single(command.Options));
    }

    [Fact]
    public void RemoveOptionByInstanceRemovesOnlyThatInstance()
    {
        StringOption removedOption = CreateStringOption("mode");
        StringOption retainedOption = CreateStringOption("mode");
        Command command = CreateCommand(options: [removedOption, retainedOption]);

        command.RemoveOption(removedOption);

        Assert.Same(retainedOption, Assert.Single(command.Options));
    }

    [Fact]
    public void RemoveOptionByNameRemovesEveryFullNameMatch()
    {
        StringOption firstMatch = CreateStringOption("mode");
        StringOption secondMatch = CreateStringOption("mode");
        StringOption retainedOption = CreateStringOption("output");
        Command command = CreateCommand(options: [firstMatch, retainedOption, secondMatch]);

        command.RemoveOption("mode");

        Assert.Same(retainedOption, Assert.Single(command.Options));
    }

    [Fact]
    public void RemovingUnknownOptionLeavesCollectionUnchanged()
    {
        StringOption option = CreateStringOption("mode");
        Command command = CreateCommand(options: [option]);

        command.RemoveOption("missing");
        command.RemoveOption(CreateStringOption("mode"));

        Assert.Same(option, Assert.Single(command.Options));
    }

    [Fact]
    public void ClearOptionsRemovesEveryOption()
    {
        Command command = CreateCommand(
            options: [CreateStringOption("mode"), CreateStringOption("output")]);

        command.ClearOptions();

        Assert.Empty(command.Options);
    }

    [Fact]
    public void SetAndClearRestOptionUpdatesReceiver()
    {
        Command command = CreateCommand();
        var restOption = new RestOption
        {
            Help = "Targets to build."
        };

        command.SetRestOption(restOption);

        Assert.Same(restOption, command.RestOption);

        command.ClearRestOption();

        Assert.Null(command.RestOption);
    }

    [Fact]
    public void FindSubCommandUsesFullNameOrSingleCharacterShortName()
    {
        Command fullNameMatch = CreateCommand(name: "bundle");
        Command shortNameMatch = CreateCommand(name: "build", shortName: 'b');
        Command root = CreateCommand(subCommands: [fullNameMatch, shortNameMatch]);

        Assert.Same(fullNameMatch, root.FindSubCommand("bundle"));
        Assert.Same(shortNameMatch, root.FindSubCommand('b'));
        Assert.Same(shortNameMatch, root.FindSubCommand("b"));
        Assert.Null(root.FindSubCommand("missing"));
    }

    [Fact]
    public void FindSubCommandUsesOrdinalCaseSensitiveNames()
    {
        Command build = CreateCommand(name: "build", shortName: 'b');
        Command root = CreateCommand(subCommands: [build]);

        Assert.Same(build, root.FindSubCommand("build"));
        Assert.Null(root.FindSubCommand("Build"));
        Assert.Same(build, root.FindSubCommand('b'));
        Assert.Null(root.FindSubCommand('B'));
    }

    [Fact]
    public void FindSubCommandRejectsNullName()
    {
        Command root = CreateCommand();

        Assert.Throws<ArgumentNullException>(() => root.FindSubCommand(null!));
    }

    [Fact]
    public void AddSubCommandAppendsCommand()
    {
        Command root = CreateCommand();
        Command build = CreateCommand(name: "build");

        root.AddSubCommand(build);

        Assert.Same(build, Assert.Single(root.SubCommands));
        Assert.Same(build, root.FindSubCommand("build"));
    }

    [Fact]
    public void SubCommandMutatorsRejectNullInstances()
    {
        Command root = CreateCommand();

        Assert.Throws<ArgumentNullException>(() => root.AddSubCommand(null!));
        Assert.Throws<ArgumentNullException>(() => root.RemoveSubCommand((Command)null!));
        Assert.Throws<ArgumentNullException>(() => root.RemoveSubCommand((string)null!));
    }

    [Fact]
    public void SubCommandInitializerRejectsNullCollectionOrElement()
    {
        Assert.Throws<ArgumentNullException>(() => _ = new Command
        {
            Name = "incant",
            Help = "Build a project.",
            Usage = "incant <command>",
            SubCommands = null!
        });
        Assert.Throws<ArgumentNullException>(() => CreateCommand(subCommands: [null!]));
    }

    [Fact]
    public void SubCommandInitializerCopiesTheConfiguredCollection()
    {
        Command build = CreateCommand(name: "build");
        List<Command> configuredCommands = [build];
        Command root = CreateCommand(subCommands: configuredCommands);

        configuredCommands.Clear();

        Assert.Same(build, Assert.Single(root.SubCommands));
    }

    [Fact]
    public void RemoveSubCommandByInstanceRemovesOnlyThatInstance()
    {
        Command removedCommand = CreateCommand(name: "build");
        Command retainedCommand = CreateCommand(name: "build");
        Command root = CreateCommand(subCommands: [removedCommand, retainedCommand]);

        root.RemoveSubCommand(removedCommand);

        Assert.Same(retainedCommand, Assert.Single(root.SubCommands));
    }

    [Fact]
    public void RemoveSubCommandByNameRemovesEveryFullNameMatch()
    {
        Command firstMatch = CreateCommand(name: "build");
        Command secondMatch = CreateCommand(name: "build");
        Command retainedCommand = CreateCommand(name: "bundle");
        Command root = CreateCommand(subCommands: [firstMatch, retainedCommand, secondMatch]);

        root.RemoveSubCommand("build");

        Assert.Same(retainedCommand, Assert.Single(root.SubCommands));
    }

    [Fact]
    public void RemovingUnknownSubCommandLeavesCollectionUnchanged()
    {
        Command build = CreateCommand(name: "build");
        Command root = CreateCommand(subCommands: [build]);

        root.RemoveSubCommand("missing");
        root.RemoveSubCommand(CreateCommand(name: "build"));

        Assert.Same(build, Assert.Single(root.SubCommands));
    }

    [Fact]
    public void ClearSubCommandsRemovesEverySubCommand()
    {
        Command root = CreateCommand(
            subCommands: [CreateCommand(name: "build"), CreateCommand(name: "clean")]);

        root.ClearSubCommands();

        Assert.Empty(root.SubCommands);
    }

    [Theory]
    [InlineData("")]
    [InlineData("m")]
    public void CheckOptionsRejectsLongNameShorterThanTwoCharacters(string name)
    {
        Command command = CreateCommand(options: [CreateStringOption(name)]);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => command.CheckOptions());

        Assert.Contains("Option name too short", exception.Message);
    }

    [Fact]
    public void CheckOptionsRejectsDuplicateLongNames()
    {
        Command command = CreateCommand(
            options:
            [
                CreateStringOption("output", 'o'),
                CreateStringOption("output", 'p')
            ]);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => command.CheckOptions());

        Assert.Contains("Duplicate option name", exception.Message);
    }

    [Fact]
    public void CheckOptionsRejectsDuplicateShortNames()
    {
        Command command = CreateCommand(
            options:
            [
                CreateStringOption("output", 'o'),
                CreateStringOption("object", 'o')
            ]);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => command.CheckOptions());

        Assert.Contains("Duplicate option short name", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("b")]
    public void CheckSubCommandsRejectsFullNameShorterThanTwoCharacters(string name)
    {
        Command root = CreateCommand(subCommands: [CreateCommand(name: name)]);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => root.CheckSubCommands());

        Assert.Contains("Sub-command name too short", exception.Message);
    }

    [Fact]
    public void CheckSubCommandsRejectsDuplicateLongNames()
    {
        Command root = CreateCommand(
            subCommands:
            [
                CreateCommand(name: "build", shortName: 'b'),
                CreateCommand(name: "build", shortName: 'c')
            ]);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => root.CheckSubCommands());

        Assert.Contains("Duplicate sub-command name", exception.Message);
    }

    [Fact]
    public void CheckSubCommandsRejectsDuplicateShortNames()
    {
        Command root = CreateCommand(
            subCommands:
            [
                CreateCommand(name: "build", shortName: 'b'),
                CreateCommand(name: "bundle", shortName: 'b')
            ]);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => root.CheckSubCommands());

        Assert.Contains("Duplicate sub-command short name", exception.Message);
    }

    [Fact]
    public void ChecksAllowNamesThatDifferOnlyByCase()
    {
        Command root = CreateCommand(
            options: [CreateStringOption("mode"), CreateStringOption("Mode")],
            subCommands: [CreateCommand(name: "build"), CreateCommand(name: "Build")]);

        root.CheckAll();
    }

    [Fact]
    public void CheckOptionsOnlyChecksDescendantsWhenRecursive()
    {
        Command build = CreateCommand(
            name: "build",
            options: [CreateStringOption("mode"), CreateStringOption("mode")]);
        Command root = CreateCommand(subCommands: [build]);

        root.CheckOptions();
        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => root.CheckOptions(true));

        Assert.Contains("Duplicate option name", exception.Message);
    }

    [Fact]
    public void CheckSubCommandsOnlyChecksDescendantsWhenRecursive()
    {
        Command build = CreateCommand(
            name: "build",
            subCommands:
            [
                CreateCommand(name: "compile"),
                CreateCommand(name: "compile")
            ]);
        Command root = CreateCommand(subCommands: [build]);

        root.CheckSubCommands();
        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => root.CheckSubCommands(true));

        Assert.Contains("Duplicate sub-command name", exception.Message);
    }

    [Fact]
    public void CheckAllChecksOptionsAndSubCommands()
    {
        Command root = CreateCommand(
            subCommands:
            [
                CreateCommand(name: "build"),
                CreateCommand(name: "build")
            ]);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => root.CheckAll());

        Assert.Contains("Duplicate sub-command name", exception.Message);
    }

    [Fact]
    public void CheckAllDoesNotApplyRulesOutsideReferenceChecks()
    {
        Command root = new()
        {
            Name = "i",
            ShortName = '-',
            Help = string.Empty,
            Usage = string.Empty,
            Options =
            [
                CreateStringOption("-mode", '-'),
                CreateStringOption("build mode", '='),
                CreateStringOption("mode=value", ' ')
            ],
            SubCommands =
            [
                CreateCommand(name: "-build", shortName: '-'),
                CreateCommand(name: "build mode", shortName: '='),
                CreateCommand(name: "mode=value", shortName: ' ')
            ]
        };

        root.CheckAll();
    }

    [Fact]
    public void InvokeChecksCompleteCommandTreeBeforeParsing()
    {
        Command build = CreateCommand(
            name: "build",
            options: [CreateStringOption("mode"), CreateStringOption("mode")]);
        Command root = CreateCommand(subCommands: [build]);
        var parser = new CommandParser
        {
            RootCommand = root
        };

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => parser.Invoke([]));

        Assert.Contains("Duplicate option name", exception.Message);
    }

    [Fact]
    public void WriteHelpUsesDefaultBannerWhenCustomBannerIsEmpty()
    {
        Command command = CreateCommand();
        var writer = new Writer();

        command.WriteHelp(writer, "Default Incant");

        Assert.Contains("Default Incant", writer.Content);
    }

    [Fact]
    public void WriteHelpIncludesConfiguredSectionsAndValues()
    {
        Command clean = CreateCommand(name: "clean", shortName: 'c');
        StringOption configuration = CreateStringOption("configuration", 'c');
        var restOption = new RestOption
        {
            Help = "Remaining project arguments."
        };
        Command command = CreateCommand(
            options: [configuration],
            restOption: restOption,
            subCommands: [clean]);
        var writer = new Writer();

        command.WriteHelp(writer, "Incant");

        Assert.Contains("Incant", writer.Content);
        Assert.Contains("Build a project.", writer.Content);
        Assert.Contains("Usage:", writer.Content);
        Assert.Contains($"Sub-Commands:{Environment.NewLine}", writer.Content);
        Assert.Contains("clean", writer.Content);
        Assert.Contains($"Options: {Environment.NewLine}", writer.Content);
        Assert.Contains("configuration", writer.Content);
        Assert.Contains($"Rest-Option: {Environment.NewLine}", writer.Content);
        Assert.Contains("Remaining project arguments.", writer.Content);
    }

    [Fact]
    public void WriteHelpUsesCommandCustomBanner()
    {
        Command command = CreateCommand(customBanner: "Custom Incant");
        var writer = new Writer();

        command.WriteHelp(writer, "Default Incant");

        Assert.Contains("Custom Incant", writer.Content);
        Assert.DoesNotContain("Default Incant", writer.Content);
    }

    [Fact]
    public void WriteHelpOmitsEmptyHelpAndUsage()
    {
        var command = new Command
        {
            Name = "incant",
            Help = string.Empty,
            Usage = string.Empty
        };
        var writer = new Writer();

        command.WriteHelp(writer, string.Empty);

        Assert.True(writer.IsEmpty);
    }

    [Fact]
    public void WriteHelpPlacesBlankLineBeforeSelections()
    {
        var option = new StringOption
        {
            Name = "configuration",
            Help = "Select a build configuration.",
            Selections = ["debug", "release"]
        };
        Command command = CreateCommand(options: [option]);
        var writer = new Writer();

        command.WriteHelp(writer, string.Empty);

        string[] lines = writer.Content.Split('\n');
        int firstSelectionLine = Array.FindIndex(
            lines,
            line => line.Contains("- debug", StringComparison.Ordinal));
        Assert.True(firstSelectionLine > 0);
        Assert.True(string.IsNullOrWhiteSpace(lines[firstSelectionLine - 1]));
    }

    [Fact]
    public void WriteHelpOmitsUnconfiguredCollectionSections()
    {
        Command command = CreateCommand();
        var writer = new Writer();

        command.WriteHelp(writer, string.Empty);

        Assert.DoesNotContain("Sub-Commands:", writer.Content);
        Assert.DoesNotContain("Options: ", writer.Content);
        Assert.DoesNotContain("Rest-Option: ", writer.Content);
    }

    [Fact]
    public void WriteHelpMarksRequiredOptionsAndRemainingArguments()
    {
        StringOption option = CreateStringOption("project");
        var restOption = new RestOption
        {
            Help = "Targets to build.",
            IsRequired = true
        };
        Command command = CreateCommand(options: [option], restOption: restOption);
        var writer = new Writer();

        command.WriteHelp(writer, string.Empty);

        Assert.Equal(2, CountOccurrences(writer.Content, "[REQUIRED] "));
        Assert.Contains("[REQUIRED] Select a value.", writer.Content);
        Assert.Contains("[REQUIRED] Targets to build.", writer.Content);
    }

    [Fact]
    public void WriteHelpOmitsSelectionBulletsForEmptySelections()
    {
        var option = new StringOption
        {
            Name = "configuration",
            Help = "Select a build configuration.",
            Selections = []
        };
        Command command = CreateCommand(options: [option]);
        var writer = new Writer();

        command.WriteHelp(writer, string.Empty);

        Assert.DoesNotContain("  - ", writer.Content);
    }

    [Fact]
    public void HelpCommandPrintsHelpInsteadOfExecuting()
    {
        int executionCount = 0;
        Command command = CreateCommand(
            isHelpCommand: true,
            execute: () =>
            {
                ++executionCount;
                return 41;
            });
        var parser = new CommandParser
        {
            RootCommand = command,
            DefaultBanner = "Incant"
        };

        Command? invokedCommand = parser.Invoke([]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(0, executionCount);
        Assert.Equal(0, parser.ExitCode);
    }

    private static StringOption CreateStringOption(string name, char? shortName = null)
    {
        return new StringOption
        {
            Name = name,
            ShortName = shortName,
            Help = "Select a value."
        };
    }

    private static int CountOccurrences(string content, string value)
    {
        int count = 0;
        int startIndex = 0;
        while ((startIndex = content.IndexOf(value, startIndex, StringComparison.Ordinal)) >= 0)
        {
            ++count;
            startIndex += value.Length;
        }

        return count;
    }

    private static Command CreateCommand(
        string name = "incant",
        char? shortName = null,
        IReadOnlyList<IOption>? options = null,
        IRestOption? restOption = null,
        IReadOnlyList<Command>? subCommands = null,
        Command.ExecuteDelegate? execute = null,
        string customBanner = "",
        bool isHelpCommand = false)
    {
        return new Command
        {
            Name = name,
            ShortName = shortName,
            Help = "Build a project.",
            Usage = $"{name} [options]",
            CustomBanner = customBanner,
            IsHelpCommand = isHelpCommand,
            Options = options ?? [],
            RestOption = restOption,
            SubCommands = subCommands ?? [],
            Execute = execute
        };
    }
}
