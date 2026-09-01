using Incant.Base.Cli;

namespace Incant.UnitTest.Base.Cli;

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

    [Fact]
    public void ExitCodeIsMinusOneBeforeFirstInvocation()
    {
        CommandParser parser = CreateParser(CreateCommand());

        Assert.Equal(-1, parser.ExitCode);
    }

    [Fact]
    public void InvokeRejectsNullArgumentSequence()
    {
        CommandParser parser = CreateParser(CreateCommand());

        Assert.Throws<ArgumentNullException>(() => parser.Invoke(null!));
        Assert.Equal(-1, parser.ExitCode);
    }

    [Fact]
    public void ExecutorExceptionPropagatesAndLeavesFailureExitCode()
    {
        var expectedException = new InvalidOperationException("execution failed");
        Command command = CreateCommand(() => throw expectedException);
        CommandParser parser = CreateParser(command);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => parser.Invoke([]));

        Assert.Same(expectedException, exception);
        Assert.Equal(-1, parser.ExitCode);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("b")]
    public void InvokeSelectsSubCommandByFullOrShortName(string commandName)
    {
        Command subCommand = CreateCommand(() => 23, name: "build", shortName: 'b');
        Command rootCommand = CreateCommand(subCommands: [subCommand]);
        CommandParser parser = CreateParser(rootCommand);

        Command? invokedCommand = parser.Invoke([commandName]);

        Assert.Same(subCommand, invokedCommand);
        Assert.Equal(23, parser.ExitCode);
    }

    [Theory]
    [InlineData("build", "run")]
    [InlineData("b", "r")]
    public void InvokeSelectsNestedSubCommands(string firstName, string secondName)
    {
        Command run = CreateCommand(() => 31, name: "run", shortName: 'r');
        Command build = CreateCommand(
            DefaultExecute,
            subCommands: [run],
            name: "build",
            shortName: 'b');
        Command root = CreateCommand(subCommands: [build]);
        CommandParser parser = CreateParser(root);

        Command? invokedCommand = parser.Invoke([firstName, secondName]);

        Assert.Same(run, invokedCommand);
        Assert.Equal(31, parser.ExitCode);
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
        Command command = CreateCommand(options: [option]);
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
        Command command = CreateCommand(options: [firstOption, secondOption]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["-ab"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(1, firstOption.ToggleCount);
        Assert.Equal(1, secondOption.ToggleCount);
    }

    [Fact]
    public void RepeatedValueOptionAssignsEveryOccurrenceInOrder()
    {
        var option = new TestOption
        {
            Name = "output",
            ShortName = 'o'
        };
        Command command = CreateCommand(options: [option]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["--output=first", "-o", "second"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(["first", "second"], option.AssignedValues);
    }

    [Fact]
    public void MissingValueDoesNotConsumeFollowingOption()
    {
        var output = new TestOption
        {
            Name = "output"
        };
        var verbose = new TestOption
        {
            Name = "verbose",
            IsToggle = true
        };
        Command command = CreateCommand(options: [output, verbose]);
        CommandParser parser = CreateParser(command);
        parser.TreatMissingArgumentAsError = false;

        Command? invokedCommand = parser.Invoke(["--output", "--verbose"]);

        Assert.Same(command, invokedCommand);
        Assert.Empty(output.AssignedValues);
        Assert.Equal(1, verbose.ToggleCount);
    }

    [Theory]
    [InlineData("--output=")]
    [InlineData("-o=")]
    public void ExplicitEmptyValueDoesNotConsumeFollowingArgument(string optionArgument)
    {
        var output = new TestOption
        {
            Name = "output",
            ShortName = 'o'
        };
        var restOption = new TestRestOption
        {
            AllowMixed = true
        };
        Command command = CreateCommand(options: [output], restOption: restOption);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([optionArgument, "tail"]);

        Assert.Null(invokedCommand);
        Assert.Empty(output.AssignedValues);
        Assert.Equal(["tail"], restOption.AssignedValues);
    }

    [Theory]
    [InlineData("--=value")]
    [InlineData("--x")]
    public void MalformedLongOptionNeverUsesShortAlias(string argument)
    {
        var option = new TestOption
        {
            Name = "execute",
            ShortName = 'x',
            IsToggle = true
        };
        Command command = CreateCommand(options: [option]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([argument]);

        Assert.Null(invokedCommand);
        Assert.Equal(0, option.ToggleCount);
        Assert.Equal(-1, parser.ExitCode);
    }

    [Theory]
    [InlineData("-=")]
    [InlineData("-=value")]
    public void MalformedShortOptionFailsWithoutThrowing(string argument)
    {
        Command command = CreateCommand();
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([argument]);

        Assert.Null(invokedCommand);
        Assert.Equal(-1, parser.ExitCode);
    }

    [Theory]
    [InlineData("-ab=")]
    [InlineData("-ab=value")]
    public void ToggleGroupWithAssignedValueFailsWithoutChangingOptions(string argument)
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
        Command command = CreateCommand(options: [firstOption, secondOption]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([argument]);

        Assert.Null(invokedCommand);
        Assert.Equal(0, firstOption.ToggleCount);
        Assert.Equal(0, secondOption.ToggleCount);
        Assert.Equal(-1, parser.ExitCode);
    }

    [Theory]
    [InlineData("-ab")]
    [InlineData("-ba")]
    public void ValueOptionInToggleGroupFailsInvocation(string argument)
    {
        var output = new TestOption
        {
            Name = "all",
            ShortName = 'a'
        };
        var brief = new TestOption
        {
            Name = "brief",
            ShortName = 'b',
            IsToggle = true
        };
        Command command = CreateCommand(options: [output, brief]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([argument]);

        Assert.Null(invokedCommand);
        Assert.Empty(output.AssignedValues);
        Assert.Equal(0, brief.ToggleCount);
    }

    [Theory]
    [InlineData("-ax")]
    [InlineData("-xa")]
    public void UnrecognizedMemberOfToggleGroupFailsWithoutChangingRecognizedOptions(string argument)
    {
        var all = new TestOption
        {
            Name = "all",
            ShortName = 'a',
            IsToggle = true
        };
        Command command = CreateCommand(options: [all]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([argument]);

        Assert.Null(invokedCommand);
        Assert.Equal(0, all.ToggleCount);
        Assert.Equal(-1, parser.ExitCode);
    }

    [Theory]
    [InlineData("-ax")]
    [InlineData("-xa")]
    public void UnrecognizedMemberOfToggleGroupCanBeHandledAsWarning(string argument)
    {
        var all = new TestOption
        {
            Name = "all",
            ShortName = 'a',
            IsToggle = true
        };
        Command command = CreateCommand(options: [all]);
        CommandParser parser = CreateParser(command);
        parser.FailOnUnrecognizedOption = false;

        Command? invokedCommand = parser.Invoke([argument]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(1, all.ToggleCount);
    }

    [Fact]
    public void InvalidToggleGroupDoesNotRollbackEarlierTokens()
    {
        var all = new TestOption
        {
            Name = "all",
            ShortName = 'a',
            IsToggle = true
        };
        var brief = new TestOption
        {
            Name = "brief",
            ShortName = 'b',
            IsToggle = true
        };
        var output = new TestOption
        {
            Name = "output",
            ShortName = 'o'
        };
        Command command = CreateCommand(options: [all, brief, output]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["-a", "-bo"]);

        Assert.Null(invokedCommand);
        Assert.Equal(1, all.ToggleCount);
        Assert.Equal(0, brief.ToggleCount);
        Assert.Empty(output.AssignedValues);
        Assert.Equal(-1, parser.ExitCode);
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
        Command command = CreateCommand(() => executionCount++, options: [option]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([]);

        Assert.Null(invokedCommand);
        Assert.Equal(0, executionCount);
        Assert.Equal(-1, parser.ExitCode);
    }

    [Fact]
    public void MissingRequiredOptionStillFailsWhenMissingValueIsOnlyAWarning()
    {
        var option = new TestOption
        {
            Name = "project",
            IsRequired = true
        };
        Command command = CreateCommand(options: [option]);
        CommandParser parser = CreateParser(command);
        parser.TreatMissingArgumentAsError = false;

        Command? invokedCommand = parser.Invoke(["--project"]);

        Assert.Null(invokedCommand);
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
        Command command = CreateCommand(options: [option]);
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
        Command command = CreateCommand(options: [option]);
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

    [Theory]
    [InlineData("unexpected")]
    [InlineData("--unexpected")]
    [InlineData("-x")]
    public void UnrecognizedInputPreventsExecutionByDefault(string argument)
    {
        int executionCount = 0;
        Command command = CreateCommand(() => executionCount++);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([argument]);

        Assert.Null(invokedCommand);
        Assert.Equal(0, executionCount);
        Assert.Equal(-1, parser.ExitCode);
    }

    [Fact]
    public void DoubleDashWithoutRestOptionPreventsExecutionByDefault()
    {
        Command command = CreateCommand();
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["--", "tail"]);

        Assert.Null(invokedCommand);
        Assert.Equal(-1, parser.ExitCode);
    }

    [Fact]
    public void DoubleDashWithoutRestOptionCanBeHandledAsWarning()
    {
        Command command = CreateCommand();
        CommandParser parser = CreateParser(command);
        parser.FailOnUnrecognizedArgument = false;

        Command? invokedCommand = parser.Invoke(["--", "tail"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(0, parser.ExitCode);
    }

    [Fact]
    public void NonMixedRestOptionReceivesEverythingAfterFirstArgument()
    {
        var restOption = new TestRestOption();
        Command command = CreateCommand(restOption: restOption);
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
        Command command = CreateCommand(options: [option], restOption: restOption);
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
        Command command = CreateCommand(restOption: restOption);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["--", "first", "--literal"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(["first", "--literal"], restOption.AssignedValues);
    }

    [Fact]
    public void TokensAfterDoubleDashRemainLiteralIncludingAnotherDoubleDash()
    {
        var restOption = new TestRestOption
        {
            RequireDoubleDash = true
        };
        Command command = CreateCommand(restOption: restOption);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["--", "--", "-x"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(["--", "-x"], restOption.AssignedValues);
    }

    [Fact]
    public void DoubleDashRestOptionRejectsArgumentBeforeSeparator()
    {
        var restOption = new TestRestOption
        {
            RequireDoubleDash = true
        };
        Command command = CreateCommand(restOption: restOption);
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

    // TODO(CLI-005): Enable after help assignments are rejected by the parser.
    /*
        [Theory]
        [InlineData("--help=")]
        [InlineData("--help=value")]
        [InlineData("-h=")]
        [InlineData("-h=value")]
        public void HelpOptionWithAssignedValueFailsInvocation(string helpOption)
        {
            int executionCount = 0;
            Command command = CreateCommand(() => executionCount++);
            CommandParser parser = CreateParser(command);

            Command? invokedCommand = parser.Invoke([helpOption]);

            Assert.Null(invokedCommand);
            Assert.Equal(0, executionCount);
            Assert.Equal(-1, parser.ExitCode);
        }
    */

    [Fact]
    public void HelpOptionTargetsSelectedSubCommand()
    {
        int rootExecutionCount = 0;
        int buildExecutionCount = 0;
        Command build = CreateCommand(() => buildExecutionCount++, name: "build");
        Command root = CreateCommand(
            () => rootExecutionCount++,
            subCommands: [build]);
        CommandParser parser = CreateParser(root);

        Command? invokedCommand = parser.Invoke(["build", "--help"]);

        Assert.Null(invokedCommand);
        Assert.Equal(0, rootExecutionCount);
        Assert.Equal(0, buildExecutionCount);
        Assert.Equal(0, parser.ExitCode);
    }

    [Fact]
    public void ReusedParserResetsExitCodeBeforeFailure()
    {
        var project = new TestOption
        {
            Name = "project",
            IsRequired = true
        };
        Command command = CreateCommand(() => 17, options: [project]);
        CommandParser parser = CreateParser(command);

        Command? firstInvocation = parser.Invoke(["--project=app"]);
        Command? secondInvocation = parser.Invoke([]);

        Assert.Same(command, firstInvocation);
        Assert.Null(secondInvocation);
        Assert.Equal(-1, parser.ExitCode);
    }

    [Fact]
    public void ReusedParserSetsHelpCommandExitCodeToZero()
    {
        Command help = new()
        {
            Name = "help",
            Help = "Print help.",
            Usage = "incant help",
            IsHelpCommand = true,
            Execute = () => 99
        };
        Command root = CreateCommand(() => 23, subCommands: [help]);
        CommandParser parser = CreateParser(root);

        Command? firstInvocation = parser.Invoke([]);
        Command? secondInvocation = parser.Invoke(["help"]);

        Assert.Same(root, firstInvocation);
        Assert.Same(help, secondInvocation);
        Assert.Equal(0, parser.ExitCode);
    }

    [Fact]
    public void CommandWithoutExecutorSucceedsWithZeroExitCode()
    {
        Command command = CreateCommand(execute: null);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(0, parser.ExitCode);
    }

    private static Command CreateCommand(
        IReadOnlyList<IOption>? options = null,
        IRestOption? restOption = null,
        IReadOnlyList<Command>? subCommands = null)
    {
        return CreateCommand(DefaultExecute, options, restOption, subCommands);
    }

    private static Command CreateCommand(
        Command.ExecuteDelegate? execute,
        IReadOnlyList<IOption>? options = null,
        IRestOption? restOption = null,
        IReadOnlyList<Command>? subCommands = null,
        string name = "incant",
        char? shortName = null)
    {
        return new Command
        {
            Name = name,
            ShortName = shortName,
            Help = "Build a project.",
            Usage = "incant [options]",
            Execute = execute,
            Options = options ?? [],
            RestOption = restOption,
            SubCommands = subCommands ?? []
        };
    }

    private static CommandParser CreateParser(Command command)
    {
        return new CommandParser
        {
            RootCommand = command,
            DefaultBanner = "Incant"
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

        public string ValueTypeName => "string";

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
        public bool IsRequired { get; init; }
        public bool AllowMixed { get; init; }
        public bool RequireDoubleDash { get; init; }
        public List<string> AssignedValues { get; private set; } = [];

        public void Assign(ParseContext context, List<string> values)
        {
            AssignedValues = [.. values];
        }
    }
}
