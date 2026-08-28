using Incant.Base.Cli;

namespace Incant.UnitTests.Cli;

public sealed class CommandParserTests
{
    [Fact]
    public void InvokeExecutesRootCommandAndExposesExitCode()
    {
        int executionCount = 0;
        Command command = CreateCommand(() =>
        {
            executionCount++;
            return 17;
        });
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(1, executionCount);
        Assert.Equal(17, parser.ExitCode);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("b")]
    public void InvokeSelectsSubCommandByFullOrShortName(string commandName)
    {
        Command subCommand = CreateCommand(() => 23);
        subCommand.Name = "build";
        subCommand.ShortName = 'b';
        Command rootCommand = CreateCommand();
        rootCommand.SubCommands.Add(subCommand);
        CommandParser parser = CreateParser(rootCommand);

        Command? invokedCommand = parser.Invoke([commandName]);

        Assert.Same(subCommand, invokedCommand);
        Assert.Equal(23, parser.ExitCode);
    }

    [Theory]
    [InlineData("--output=result", null)]
    [InlineData("--output", "result")]
    [InlineData("-o=result", null)]
    [InlineData("-o", "result")]
    public void ValueOptionAcceptsLongAndShortForms(string optionArgument, string? followingArgument)
    {
        var option = new TestOption
        {
            Name = "output",
            ShortName = 'o'
        };
        Command command = CreateCommand();
        command.Options.Add(option);
        CommandParser parser = CreateParser(command);
        string[] arguments = followingArgument == null
            ? [optionArgument]
            : [optionArgument, followingArgument];

        Command? invokedCommand = parser.Invoke(arguments);

        Assert.Same(command, invokedCommand);
        Assert.Equal(["result"], option.AssignedValues);
    }

    [Fact]
    public void ShortToggleGroupTogglesEveryMatchingOption()
    {
        var firstOption = new TestOption
        {
            Name = "all",
            ShortName = 'a',
            IsToggle = true
        };
        var secondOption = new TestOption
        {
            Name = "brief",
            ShortName = 'b',
            IsToggle = true
        };
        Command command = CreateCommand();
        command.Options.Add(firstOption);
        command.Options.Add(secondOption);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["-ab"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(1, firstOption.ToggleCount);
        Assert.Equal(1, secondOption.ToggleCount);
    }

    [Fact]
    public void RequiredOptionPreventsExecutionWhenMissing()
    {
        int executionCount = 0;
        var option = new TestOption
        {
            Name = "project",
            IsRequired = true
        };
        Command command = CreateCommand(() => executionCount++);
        command.Options.Add(option);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([]);

        Assert.Null(invokedCommand);
        Assert.Equal(0, executionCount);
        Assert.Equal(-1, parser.ExitCode);
    }

    [Fact]
    public void SelectionOptionRejectsValueOutsideConfiguredChoices()
    {
        var option = new TestOption
        {
            Name = "configuration",
            Selections = ["debug", "release"]
        };
        Command command = CreateCommand();
        command.Options.Add(option);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["--configuration", "profile"]);

        Assert.Null(invokedCommand);
        Assert.Empty(option.AssignedValues);
    }

    [Fact]
    public void MissingOptionArgumentCanBeHandledAsWarning()
    {
        var option = new TestOption
        {
            Name = "output"
        };
        Command command = CreateCommand();
        command.Options.Add(option);
        CommandParser parser = CreateParser(command);
        parser.TreatMissingArgumentAsError = false;

        Command? invokedCommand = parser.Invoke(["--output"]);

        Assert.Same(command, invokedCommand);
        Assert.Empty(option.AssignedValues);
    }

    [Fact]
    public void UnrecognizedArgumentsCanBeHandledAsWarnings()
    {
        Command command = CreateCommand();
        CommandParser parser = CreateParser(command);
        parser.FailOnUnrecognizedArgument = false;

        Command? invokedCommand = parser.Invoke(["unexpected"]);

        Assert.Same(command, invokedCommand);
    }

    [Fact]
    public void UnrecognizedOptionsCanBeHandledAsWarnings()
    {
        Command command = CreateCommand();
        CommandParser parser = CreateParser(command);
        parser.FailOnUnrecognizedOption = false;

        Command? invokedCommand = parser.Invoke(["--unexpected", "-x"]);

        Assert.Same(command, invokedCommand);
    }

    [Fact]
    public void NonMixedRestOptionReceivesEverythingAfterFirstArgument()
    {
        var restOption = new TestRestOption();
        Command command = CreateCommand();
        command.RestOption = restOption;
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["first", "--literal", "second"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(["first", "--literal", "second"], restOption.AssignedValues);
    }

    [Fact]
    public void MixedRestOptionAllowsOptionsBetweenRestArguments()
    {
        var option = new TestOption
        {
            Name = "output"
        };
        var restOption = new TestRestOption
        {
            AllowMixed = true
        };
        Command command = CreateCommand();
        command.Options.Add(option);
        command.RestOption = restOption;
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["first", "--output", "result", "second"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(["result"], option.AssignedValues);
        Assert.Equal(["first", "second"], restOption.AssignedValues);
    }

    [Fact]
    public void DoubleDashRestOptionReceivesOnlyArgumentsAfterSeparator()
    {
        var restOption = new TestRestOption
        {
            RequireDoubleDash = true
        };
        Command command = CreateCommand();
        command.RestOption = restOption;
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["--", "first", "--literal"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(["first", "--literal"], restOption.AssignedValues);
    }

    [Fact]
    public void DoubleDashRestOptionRejectsArgumentBeforeSeparator()
    {
        var restOption = new TestRestOption
        {
            RequireDoubleDash = true
        };
        Command command = CreateCommand();
        command.RestOption = restOption;
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["first"]);

        Assert.Null(invokedCommand);
        Assert.Empty(restOption.AssignedValues);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpOptionPrintsHelpWithoutExecutingCommand(string helpOption)
    {
        int executionCount = 0;
        Command command = CreateCommand(() => executionCount++);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([helpOption]);

        Assert.Null(invokedCommand);
        Assert.Equal(0, executionCount);
        Assert.Equal(0, parser.ExitCode);
    }

    [Fact]
    public void CommandWithoutExecutorCanSucceedWithoutPrintingHelp()
    {
        Command command = CreateCommand(execute: null);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(-1, parser.ExitCode);
    }

    private static Command CreateCommand()
    {
        return CreateCommand(DefaultExecute);
    }

    private static Command CreateCommand(Command.ExecuteDelegate? execute)
    {
        return new Command
        {
            Name = "incant",
            Help = "Build a project.",
            Usage = "incant [options]",
            Execute = execute
        };
    }

    private static CommandParser CreateParser(Command command)
    {
        return new CommandParser
        {
            RootCommand = command,
            Banner = "Incant",
            PrintHelpWhenCommandHasNoExecutor = false
        };
    }

    private static int DefaultExecute()
    {
        return 0;
    }

    private sealed class TestOption : IOption
    {
        public required string Name { get; init; }
        public char? ShortName { get; init; }
        public string Help { get; init; } = "Test option.";
        public bool IsRequired { get; init; }
        public IEnumerable<string>? Selections { get; init; }
        public bool IsToggle { get; init; }
        public string DefaultValue { get; init; } = string.Empty;
        public List<string> AssignedValues { get; } = [];
        public int ToggleCount { get; private set; }

        public string DumpTypeName()
        {
            return "string";
        }

        public void Assign(ParseContext context, string value)
        {
            AssignedValues.Add(value);
        }

        public void Toggle(ParseContext context)
        {
            ToggleCount++;
        }
    }

    private sealed class TestRestOption : IRestOption
    {
        public string Help { get; init; } = "Test rest option.";
        public bool AllowMixed { get; init; }
        public bool RequireDoubleDash { get; init; }
        public List<string> AssignedValues { get; private set; } = [];

        public void Assign(ParseContext context, List<string> values)
        {
            AssignedValues = [.. values];
        }
    }
}

public sealed class CommandTests
{
    [Fact]
    public void CheckOptionsRejectsDuplicateLongNames()
    {
        Command command = CreateCommandWithOptions(
            new TestOption("output", 'o'),
            new TestOption("output", 'p'));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(command.CheckOptions);

        Assert.Contains("Duplicate option name", exception.Message);
    }

    [Fact]
    public void CheckOptionsRejectsDuplicateShortNames()
    {
        Command command = CreateCommandWithOptions(
            new TestOption("output", 'o'),
            new TestOption("object", 'o'));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(command.CheckOptions);

        Assert.Contains("Duplicate short option name", exception.Message);
    }

    [Fact]
    public void CheckSubCommandsRejectsDuplicateLongNames()
    {
        var command = new Command
        {
            SubCommands =
            [
                new Command { Name = "build", ShortName = 'b' },
                new Command { Name = "build", ShortName = 'c' }
            ]
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(command.CheckSubCommands);

        Assert.Contains("Duplicate sub command name", exception.Message);
    }

    [Fact]
    public void CheckSubCommandsRejectsDuplicateShortNames()
    {
        var command = new Command
        {
            SubCommands =
            [
                new Command { Name = "build", ShortName = 'b' },
                new Command { Name = "bundle", ShortName = 'b' }
            ]
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(command.CheckSubCommands);

        Assert.Contains("Duplicate short sub command name", exception.Message);
    }

    [Fact]
    public void WriteHelpIncludesConfiguredSectionsAndValues()
    {
        var command = new Command
        {
            Help = "Build a project.",
            Usage = "incant build [options]",
            SubCommands =
            [
                new Command
                {
                    Name = "clean",
                    ShortName = 'c',
                    Help = "Remove generated files."
                }
            ],
            Options =
            [
                new TestOption("configuration", 'c')
            ],
            RestOption = new TestRestOption()
        };
        var writer = new Writer();

        command.WriteHelp(writer, "Incant");

        Assert.Contains("Incant", writer.Content);
        Assert.Contains("Build a project.", writer.Content);
        Assert.Contains("Usage:", writer.Content);
        Assert.Contains("incant build [options]", writer.Content);
        Assert.Contains("Sub Commands:", writer.Content);
        Assert.Contains("clean", writer.Content);
        Assert.Contains("Options:", writer.Content);
        Assert.Contains("configuration", writer.Content);
        Assert.Contains("Rest Options:", writer.Content);
    }

    private static Command CreateCommandWithOptions(params IOption[] options)
    {
        return new Command
        {
            Options = [.. options]
        };
    }

    private sealed class TestOption(string name, char? shortName) : IOption
    {
        public string Name { get; } = name;
        public char? ShortName { get; } = shortName;
        public string Help => "Test option.";
        public bool IsRequired => false;
        public IEnumerable<string>? Selections => null;
        public bool IsToggle => false;
        public string DefaultValue => "debug";

        public string DumpTypeName()
        {
            return "string";
        }

        public void Assign(ParseContext context, string value)
        {
        }

        public void Toggle(ParseContext context)
        {
        }
    }

    private sealed class TestRestOption : IRestOption
    {
        public string Help => "Remaining project arguments.";
        public bool AllowMixed => false;
        public bool RequireDoubleDash => false;

        public void Assign(ParseContext context, List<string> values)
        {
        }
    }
}

public sealed class ParseContextTests
{
    [Fact]
    public void DiagnosticsSetFlagsAndAppendStyledMessages()
    {
        var context = new ParseContext
        {
            Tokens = new TokenList(Array.Empty<string>())
        };

        context.Warning("warning message");
        context.Error("error message");

        Assert.True(context.AnyWarning);
        Assert.True(context.AnyError);
        Assert.Contains("Warning:", context.Writer.Content);
        Assert.Contains("warning message", context.Writer.Content);
        Assert.Contains("Error:", context.Writer.Content);
        Assert.Contains("error message", context.Writer.Content);
    }

    [Fact]
    public void StoreCommandTokensSnapshotsUnusedTokensOnlyOnce()
    {
        var context = new ParseContext
        {
            Tokens = new TokenList(["matched", "unused"])
        };
        context.Tokens.Match();

        context.StoreCommandTokens();
        context.Tokens.Match();
        context.StoreCommandTokens();

        Assert.NotNull(context.CommandTokens);
        Assert.Equal(["unused"], context.CommandTokens.AllTokens.Select(token => token.Raw));
    }
}
