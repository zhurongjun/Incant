using Incant.Base.Cli;

namespace Incant.UnitTest.Base.Cli;

public sealed class OptionTests
{
    [Fact]
    public void InvokeParsesEveryValueOptionWithoutReflection()
    {
        var signed = new IntOption
        {
            Name = "signed",
            Help = "A signed value.",
            Value = 7
        };
        var unsigned = new UIntOption
        {
            Name = "unsigned",
            Help = "An unsigned value.",
            Value = 8
        };
        var ratio = new FloatOption
        {
            Name = "ratio",
            Help = "A single-precision value.",
            Value = 0.5F
        };
        var threshold = new DoubleOption
        {
            Name = "threshold",
            Help = "A double-precision value.",
            Value = 0.25
        };
        var name = new StringOption
        {
            Name = "name",
            Help = "A text value.",
            Value = "default"
        };
        Command command = CreateCommand(options: [signed, unsigned, ratio, threshold, name]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(
        [
            "--signed=-12",
            "--unsigned=42",
            "--ratio=1.25",
            "--threshold=2.5",
            "--name=sample"
        ]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(-12, signed.Value);
        Assert.Equal(42U, unsigned.Value);
        Assert.Equal(1.25F, ratio.Value);
        Assert.Equal(2.5, threshold.Value);
        Assert.Equal("sample", name.Value);
        Assert.True(signed.WasProvided);
        Assert.True(unsigned.WasProvided);
        Assert.True(ratio.WasProvided);
        Assert.True(threshold.WasProvided);
        Assert.True(name.WasProvided);
    }

    [Fact]
    public void IntegralOptionsAcceptTheirBoundaryValues()
    {
        var minimumSigned = new IntOption
        {
            Name = "minimum-signed",
            Help = "Minimum signed value."
        };
        var maximumSigned = new IntOption
        {
            Name = "maximum-signed",
            Help = "Maximum signed value."
        };
        var maximumUnsigned = new UIntOption
        {
            Name = "maximum-unsigned",
            Help = "Maximum unsigned value."
        };
        Command command = CreateCommand(options: [minimumSigned, maximumSigned, maximumUnsigned]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(
        [
            $"--minimum-signed={int.MinValue}",
            $"--maximum-signed={int.MaxValue}",
            $"--maximum-unsigned={uint.MaxValue}"
        ]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(int.MinValue, minimumSigned.Value);
        Assert.Equal(int.MaxValue, maximumSigned.Value);
        Assert.Equal(uint.MaxValue, maximumUnsigned.Value);
    }

    [Theory]
    [InlineData("int", "2147483648")]
    [InlineData("int", "-2147483649")]
    [InlineData("uint", "4294967296")]
    [InlineData("uint", "-1")]
    public void IntegralOptionsRejectOverflowAndOutOfRangeValues(string optionName, string value)
    {
        Option option = optionName switch
        {
            "int" => new IntOption
            {
                Name = optionName,
                Help = "A signed integer.",
                IsRequired = false
            },
            "uint" => new UIntOption
            {
                Name = optionName,
                Help = "An unsigned integer.",
                IsRequired = false
            },
            _ => throw new InvalidOperationException($"Unknown test option '{optionName}'.")
        };
        Command command = CreateCommand(options: [option]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([$"--{optionName}={value}"]);

        Assert.Null(invokedCommand);
        Assert.False(option.WasProvided);
        Assert.Equal(-1, parser.ExitCode);
    }

    [Fact]
    public void FloatingPointOptionsAcceptExponentAndSpecialValues()
    {
        var ratio = new FloatOption
        {
            Name = "ratio",
            Help = "A ratio."
        };
        var threshold = new DoubleOption
        {
            Name = "threshold",
            Help = "A threshold."
        };
        var limit = new DoubleOption
        {
            Name = "limit",
            Help = "A limit."
        };
        Command command = CreateCommand(options: [ratio, threshold, limit]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(
            ["--ratio=1.25e2", "--threshold=NaN", "--limit=Infinity"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(125F, ratio.Value);
        Assert.True(double.IsNaN(threshold.Value));
        Assert.Equal(double.PositiveInfinity, limit.Value);
    }

    [Theory]
    [InlineData("--value")]
    [InlineData("-v")]
    public void SeparatedNegativeIntegerValueIsConsumed(string optionArgument)
    {
        var value = new IntOption
        {
            Name = "value",
            ShortName = 'v',
            Help = "A signed value.",
            IsRequired = false
        };
        Command command = CreateCommand(options: [value]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([optionArgument, "-9"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(-9, value.Value);
        Assert.True(value.WasProvided);
        Assert.Equal(0, parser.ExitCode);
    }

    [Theory]
    [InlineData("--value")]
    [InlineData("-v")]
    public void SeparatedNegativeFloatingPointValueIsConsumed(string optionArgument)
    {
        var value = new DoubleOption
        {
            Name = "value",
            ShortName = 'v',
            Help = "A floating-point value.",
            IsRequired = false
        };
        Command command = CreateCommand(options: [value]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([optionArgument, "-1.25e2"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(-125D, value.Value);
        Assert.True(value.WasProvided);
        Assert.Equal(0, parser.ExitCode);
    }

    [Theory]
    [InlineData("--value=-9")]
    [InlineData("-v=-9")]
    public void InlineNegativeIntegerValueIsConsumed(string optionArgument)
    {
        var value = new IntOption
        {
            Name = "value",
            ShortName = 'v',
            Help = "A signed value.",
            IsRequired = false
        };
        Command command = CreateCommand(options: [value]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([optionArgument]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(-9, value.Value);
        Assert.True(value.WasProvided);
        Assert.Equal(0, parser.ExitCode);
    }

    [Theory]
    [InlineData("int", "1.5")]
    [InlineData("uint", "-1")]
    [InlineData("float", "1,5")]
    [InlineData("double", "invalid")]
    public void InvalidNumericValuePreventsExecution(string optionName, string value)
    {
        Option option = optionName switch
        {
            "int" => new IntOption { Name = optionName, Help = "An integer." },
            "uint" => new UIntOption { Name = optionName, Help = "An unsigned integer." },
            "float" => new FloatOption { Name = optionName, Help = "A float." },
            "double" => new DoubleOption { Name = optionName, Help = "A double." },
            _ => throw new InvalidOperationException($"Unknown test option '{optionName}'.")
        };
        int executionCount = 0;
        Command command = CreateCommand(
            options: [option],
            execute: () => executionCount++);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([$"--{optionName}={value}"]);

        Assert.Null(invokedCommand);
        Assert.False(option.WasProvided);
        Assert.Equal(0, executionCount);
    }

    [Fact]
    public void InvalidNumericDiagnosticIncludesRejectedInput()
    {
        var value = new IntOption
        {
            Name = "value",
            Help = "A signed value.",
            IsRequired = false,
            Value = 42
        };
        IOption option = value;
        var context = new ParseContext
        {
            Tokens = new TokenList(Array.Empty<string>())
        };

        option.Assign(context, "invalid");

        Assert.True(context.AnyError);
        Assert.Contains("got 'invalid'", context.Writer.Content);
        Assert.DoesNotContain("got '42'", context.Writer.Content);
        Assert.Equal(42, value.Value);
        Assert.False(value.WasProvided);
    }

    [Fact]
    public void NullableOptionsAreOptionalAndParseEverySupportedValueType()
    {
        var signed = new NullableIntOption
        {
            Name = "signed",
            Help = "An optional signed value."
        };
        var unsigned = new NullableUIntOption
        {
            Name = "unsigned",
            Help = "An optional unsigned value."
        };
        var ratio = new NullableFloatOption
        {
            Name = "ratio",
            Help = "An optional single-precision value."
        };
        var threshold = new NullableDoubleOption
        {
            Name = "threshold",
            Help = "An optional double-precision value."
        };
        var name = new NullableStringOption
        {
            Name = "name",
            Help = "An optional text value."
        };
        Command command = CreateCommand(options: [signed, unsigned, ratio, threshold, name]);
        CommandParser parser = CreateParser(command);

        Command? firstInvocation = parser.Invoke([]);

        Assert.Same(command, firstInvocation);
        Assert.Null(signed.Value);
        Assert.Null(unsigned.Value);
        Assert.Null(ratio.Value);
        Assert.Null(threshold.Value);
        Assert.Null(name.Value);
        Assert.False(signed.IsRequired);
        Assert.False(unsigned.IsRequired);
        Assert.False(ratio.IsRequired);
        Assert.False(threshold.IsRequired);
        Assert.False(name.IsRequired);

        Command? secondInvocation = parser.Invoke(
        [
            "--signed=-12",
            "--unsigned=42",
            "--ratio=1.25",
            "--threshold=2.5",
            "--name=sample"
        ]);

        Assert.Same(command, secondInvocation);
        Assert.Equal(-12, signed.Value);
        Assert.Equal(42U, unsigned.Value);
        Assert.Equal(1.25F, ratio.Value);
        Assert.Equal(2.5, threshold.Value);
        Assert.Equal("sample", name.Value);
    }

    [Fact]
    public void FlagOptionBecomesTrueWhenSupplied()
    {
        var verbose = new FlagOption
        {
            Name = "verbose",
            ShortName = 'v',
            Help = "Print verbose output."
        };
        Command command = CreateCommand(options: [verbose]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["-v"]);

        Assert.Same(command, invokedCommand);
        Assert.True(verbose.Value);
        Assert.True(verbose.WasProvided);
    }

    [Theory]
    [InlineData("--verbose=false")]
    [InlineData("--verbose=")]
    [InlineData("-v=false")]
    [InlineData("-v=")]
    public void FlagOptionRejectsAnAssignedValue(string argument)
    {
        var verbose = new FlagOption
        {
            Name = "verbose",
            ShortName = 'v',
            Help = "Print verbose output."
        };
        Command command = CreateCommand(options: [verbose]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([argument]);

        Assert.Null(invokedCommand);
        Assert.False(verbose.Value);
        Assert.False(verbose.WasProvided);
    }

    [Fact]
    public void RequiredFlagMustBeProvided()
    {
        var verbose = new FlagOption
        {
            Name = "verbose",
            Help = "Print verbose output."
        };
        Command command = CreateCommand(options: [verbose]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([]);

        Assert.Null(invokedCommand);
        Assert.False(verbose.Value);
        Assert.False(verbose.WasProvided);
    }

    [Fact]
    public void OptionalFlagCanBeOmitted()
    {
        var verbose = new FlagOption
        {
            Name = "verbose",
            Help = "Print verbose output.",
            IsRequired = false
        };
        Command command = CreateCommand(options: [verbose]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([]);

        Assert.Same(command, invokedCommand);
        Assert.False(verbose.Value);
        Assert.False(verbose.WasProvided);
    }

    [Fact]
    public void ValueAndFlagOptionsRejectTheWrongAssignmentOperation()
    {
        IOption valueOption = new StringOption
        {
            Name = "output",
            Help = "Output path."
        };
        IOption flagOption = new FlagOption
        {
            Name = "verbose",
            Help = "Print verbose output."
        };
        var context = new ParseContext
        {
            Tokens = new TokenList(Array.Empty<string>())
        };

        Assert.Throws<InvalidOperationException>(() => valueOption.Toggle(context));
        Assert.Throws<InvalidOperationException>(() => flagOption.Assign(context, "true"));
    }

    [Fact]
    public void FlagOptionDoesNotExposeValueSelections()
    {
        var verbose = new FlagOption
        {
            Name = "verbose",
            Help = "Print verbose output.",
            Selections = [false, true]
        };
        IOption option = verbose;

        Assert.Null(option.Selections);
    }

    [Fact]
    public void StaticSelectionsTakePrecedenceOverProvider()
    {
        int providerCallCount = 0;
        var mode = new StringOption
        {
            Name = "mode",
            Help = "Select a mode.",
            Value = "release",
            Selections = ["debug", "release"],
            SelectionProvider = () =>
            {
                providerCallCount++;
                return ["profile"];
            }
        };
        Command command = CreateCommand(options: [mode]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["--mode=debug"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal("debug", mode.Value);
        Assert.Equal(0, providerCallCount);
    }

    [Fact]
    public void SelectionProviderSuppliesDynamicChoices()
    {
        int providerCallCount = 0;
        var mode = new StringOption
        {
            Name = "mode",
            Help = "Select a mode.",
            SelectionProvider = () =>
            {
                providerCallCount++;
                return ["debug", "release"];
            }
        };
        Command command = CreateCommand(options: [mode]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["--mode=release"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal("release", mode.Value);
        Assert.Equal(1, providerCallCount);
    }

    [Fact]
    public void EmptyDynamicSelectionsLeaveValuesUnrestricted()
    {
        var mode = new StringOption
        {
            Name = "mode",
            Help = "Select a mode.",
            SelectionProvider = () => []
        };
        Command command = CreateCommand(options: [mode]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["--mode=custom"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal("custom", mode.Value);
    }

    [Fact]
    public void NullSelectionCollectionIsRejectedWhenOptionIsUsed()
    {
        var mode = new StringOption
        {
            Name = "mode",
            Help = "Select a mode.",
            Selections = null!
        };
        CommandParser parser = CreateParser(CreateCommand(options: [mode]));

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => parser.Invoke(["--mode=debug"]));

        Assert.Contains("must not be null", exception.Message);
    }

    [Fact]
    public void NullSelectionProviderResultIsRejectedWhenOptionIsUsed()
    {
        var mode = new StringOption
        {
            Name = "mode",
            Help = "Select a mode.",
            SelectionProvider = () => null!
        };
        CommandParser parser = CreateParser(CreateCommand(options: [mode]));

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => parser.Invoke(["--mode=debug"]));

        Assert.Contains("returned null", exception.Message);
    }

    [Fact]
    public void NullSelectionElementIsRejectedWhenOptionIsUsed()
    {
        var mode = new StringOption
        {
            Name = "mode",
            Help = "Select a mode.",
            Selections = ["debug", null!]
        };
        CommandParser parser = CreateParser(CreateCommand(options: [mode]));

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => parser.Invoke(["--mode=debug"]));

        Assert.Contains("null selection", exception.Message);
    }

    [Fact]
    public void StringSelectionsUseOrdinalCaseSensitiveComparison()
    {
        var mode = new StringOption
        {
            Name = "mode",
            Help = "Select a mode.",
            IsRequired = false,
            Selections = ["debug"]
        };
        CommandParser parser = CreateParser(CreateCommand(options: [mode]));

        Command? invokedCommand = parser.Invoke(["--mode=Debug"]);

        Assert.Null(invokedCommand);
        Assert.False(mode.WasProvided);
    }

    [Fact]
    public void ValueOutsideConfiguredSelectionsIsRejected()
    {
        var mode = new StringOption
        {
            Name = "mode",
            Help = "Select a mode.",
            Value = "debug",
            Selections = ["debug", "release"]
        };
        Command command = CreateCommand(options: [mode]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["--mode=profile"]);

        Assert.Null(invokedCommand);
        Assert.Equal("debug", mode.Value);
        Assert.False(mode.WasProvided);
    }

    [Fact]
    public void NumericSelectionsUseTheirInvariantCommandLineRepresentation()
    {
        var jobs = new UIntOption
        {
            Name = "jobs",
            Help = "Maximum parallel jobs.",
            Selections = [1, 2, 4]
        };
        Command command = CreateCommand(options: [jobs]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["--jobs=2"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(2U, jobs.Value);
    }

    [Fact]
    public void NonNullableValueOptionIsRequiredByDefault()
    {
        var project = new StringOption
        {
            Name = "project",
            Help = "Project to build."
        };
        int executionCount = 0;
        Command command = CreateCommand(
            options: [project],
            execute: () => executionCount++);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([]);

        Assert.Null(invokedCommand);
        Assert.False(project.WasProvided);
        Assert.Equal(0, executionCount);
    }

    [Fact]
    public void NonNullableValueOptionCanBeConfiguredAsOptional()
    {
        var project = new StringOption
        {
            Name = "project",
            Help = "Project to build.",
            IsRequired = false,
            Value = "default"
        };
        Command command = CreateCommand(options: [project]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([]);

        Assert.Same(command, invokedCommand);
        Assert.Equal("default", project.Value);
        Assert.False(project.WasProvided);
        Assert.Equal(0, parser.ExitCode);
    }

    [Theory]
    [InlineData("--name=")]
    [InlineData("-n=")]
    public void ExplicitEmptyValueIsRejected(string argument)
    {
        var name = new StringOption
        {
            Name = "name",
            ShortName = 'n',
            Help = "A text value.",
            IsRequired = false,
            Value = "default"
        };
        Command command = CreateCommand(options: [name]);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([argument]);

        Assert.Null(invokedCommand);
        Assert.Equal("default", name.Value);
        Assert.False(name.WasProvided);
        Assert.Equal(-1, parser.ExitCode);
    }

    [Fact]
    public void SingleCharacterLongOptionIsRejectedInsteadOfUsingShortAlias()
    {
        var verbose = new FlagOption
        {
            Name = "verbose",
            ShortName = 'v',
            Help = "Print verbose output.",
            IsRequired = false
        };
        Command command = CreateCommand(options: [verbose]);
        CommandParser parser = CreateParser(command);

        Command? longInvocation = parser.Invoke(["--v"]);

        Assert.Null(longInvocation);
        Assert.False(verbose.Value);
        Assert.False(verbose.WasProvided);

        Command? shortInvocation = parser.Invoke(["-v"]);

        Assert.Same(command, shortInvocation);
        Assert.True(verbose.Value);
        Assert.True(verbose.WasProvided);
    }

    [Fact]
    public void HelpKeepsTheConfiguredDefaultAfterParsing()
    {
        var mode = new StringOption
        {
            Name = "mode",
            Help = "Select a mode.",
            Value = "debug"
        };
        Command command = CreateCommand(options: [mode]);
        CommandParser parser = CreateParser(command);

        parser.Invoke(["--mode=release"]);
        var writer = new Writer();
        command.WriteHelp(writer, "Incant");

        Assert.Equal("release", mode.Value);
        Assert.Contains("[\"debug\"]", writer.Content);
        Assert.DoesNotContain("[\"release\"]", writer.Content);
    }

    [Fact]
    public void DefaultValuesUseInvariantCommandLineRepresentations()
    {
        var signed = new IntOption
        {
            Name = "signed",
            Help = "A signed value.",
            Value = -12
        };
        var ratio = new FloatOption
        {
            Name = "ratio",
            Help = "A ratio.",
            Value = 1.25F
        };
        var optional = new NullableDoubleOption
        {
            Name = "optional",
            Help = "An optional value."
        };
        var verbose = new FlagOption
        {
            Name = "verbose",
            Help = "Print verbose output.",
            Value = true
        };

        Assert.Equal("-12", signed.DefaultValue);
        Assert.Equal("1.25", ratio.DefaultValue);
        Assert.Equal("null", optional.DefaultValue);
        Assert.Equal("true", verbose.DefaultValue);
    }

    [Fact]
    public void StringDefaultValueEscapesBackslashesAndQuotes()
    {
        var value = new StringOption
        {
            Name = "value",
            Help = "A text value.",
            Value = "a\\b\"c"
        };
        var optionalValue = new NullableStringOption
        {
            Name = "optional-value",
            Help = "An optional text value.",
            Value = "text"
        };

        Assert.Equal("\"a\\\\b\\\"c\"", value.DefaultValue);
        Assert.Equal("\"text\"", optionalValue.DefaultValue);
    }

    [Fact]
    public void NonNullableStringOptionRejectsNullInitialValue()
    {
        Assert.Throws<ArgumentNullException>(() => _ = new StringOption
        {
            Name = "value",
            Help = "A text value.",
            Value = null!
        });
    }

    [Fact]
    public void RequiredRestOptionRejectsAnEmptyArgumentList()
    {
        var targets = new RestOption
        {
            Help = "Targets to build.",
            IsRequired = true
        };
        int executionCount = 0;
        Command command = CreateCommand(
            restOption: targets,
            execute: () => executionCount++);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([]);

        Assert.Null(invokedCommand);
        Assert.Empty(targets.Values);
        Assert.False(targets.WasProvided);
        Assert.Equal(0, executionCount);
    }

    [Fact]
    public void RestOptionCanRequireTheDoubleDashSeparator()
    {
        var targets = new RestOption
        {
            Help = "Targets to build.",
            RequireDoubleDash = true
        };
        Command command = CreateCommand(restOption: targets);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["--", "-app", "--tests"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal(["-app", "--tests"], targets.Values);
        Assert.True(targets.WasProvided);
    }

    [Fact]
    public void RequiredRestOptionRejectsSeparatorWithoutFollowingValues()
    {
        var targets = new RestOption
        {
            Help = "Targets to build.",
            IsRequired = true,
            RequireDoubleDash = true
        };
        Command command = CreateCommand(restOption: targets);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke(["--"]);

        Assert.Null(invokedCommand);
        Assert.Empty(targets.Values);
        Assert.False(targets.WasProvided);
    }

    [Fact]
    public void ReusedRestOptionReplacesValuesWithCurrentInvocation()
    {
        var targets = new RestOption
        {
            Help = "Targets to build.",
            AllowMixed = true
        };
        Command command = CreateCommand(restOption: targets);
        CommandParser parser = CreateParser(command);

        Command? firstInvocation = parser.Invoke(["first", "second"]);
        Command? secondInvocation = parser.Invoke([]);

        Assert.Same(command, firstInvocation);
        Assert.Same(command, secondInvocation);
        Assert.Empty(targets.Values);
        Assert.False(targets.WasProvided);
    }

    [Fact]
    public void RestOptionPreservesEmptyAndOptionLikeArguments()
    {
        var targets = new RestOption
        {
            Help = "Targets to build."
        };
        Command command = CreateCommand(restOption: targets);
        CommandParser parser = CreateParser(command);

        Command? invokedCommand = parser.Invoke([string.Empty, "--literal", "tail"]);

        Assert.Same(command, invokedCommand);
        Assert.Equal([string.Empty, "--literal", "tail"], targets.Values);
    }

    [Fact]
    public void DeclarativeCommandExposesTypedValuesToItsExecutor()
    {
        var mode = new StringOption
        {
            Name = "mode",
            ShortName = 'm',
            Help = "Build configuration.",
            Value = "debug",
            Selections = ["debug", "release"]
        };
        var jobs = new UIntOption
        {
            Name = "jobs",
            ShortName = 'j',
            Help = "Maximum parallel jobs.",
            Value = 1
        };
        var verbose = new FlagOption
        {
            Name = "verbose",
            ShortName = 'v',
            Help = "Print verbose output."
        };
        var targets = new RestOption
        {
            Help = "Targets to build.",
            IsRequired = true,
            AllowMixed = true
        };
        string? observedValues = null;
        var build = new Command
        {
            Name = "build",
            ShortName = 'b',
            Help = "Build selected targets.",
            Usage = "incant build [options] <targets>",
            Options = [mode, jobs, verbose],
            RestOption = targets,
            Execute = () =>
            {
                observedValues =
                    $"{mode.Value}:{jobs.Value}:{verbose.Value}:{string.Join(',', targets.Values)}";
                return 19;
            }
        };
        var root = new Command
        {
            Name = "incant",
            Help = "Build C++ projects.",
            Usage = "incant <command>",
            SubCommands = [build]
        };
        CommandParser parser = CreateParser(root);

        Command? invokedCommand = parser.Invoke(
            ["build", "--mode=release", "-j", "8", "-v", "app", "tests"]);

        Assert.Same(build, invokedCommand);
        Assert.Equal("release:8:True:app,tests", observedValues);
        Assert.Equal(19, parser.ExitCode);
        Assert.Equal(["app", "tests"], targets.Values);
        Assert.True(targets.WasProvided);
    }

    private static Command CreateCommand(
        IReadOnlyList<IOption>? options = null,
        IRestOption? restOption = null,
        Command.ExecuteDelegate? execute = null)
    {
        return new Command
        {
            Name = "incant",
            Help = "Build a project.",
            Usage = "incant [options]",
            Options = options ?? [],
            RestOption = restOption,
            Execute = execute
        };
    }

    private static CommandParser CreateParser(Command command)
    {
        return new CommandParser
        {
            RootCommand = command
        };
    }
}
