using System.Globalization;
using Incant.Base.Cli;
using Incant.Core.Toolchains;

/// <summary>Builds and parses the AutoTest command tree with the shared Base CLI framework.</summary>
internal static class AutoTestCommandLine
{
    /// <summary>Parses one AutoTest invocation without performing discovery or verification.</summary>
    internal static AutoTestParseResult Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        AutoTestCommand? selectedCommand = null;
        Command discoverCommand = CreateDiscoverCommand(command => selectedCommand = command);
        Command verifyCommand = CreateVerifyCommand(command => selectedCommand = command);
        var rootCommand = new Command
        {
            Name = "Incant.AutoTest.Toolchains",
            Help = "Discovers and verifies toolchains installed on the current host.",
            Usage = "Incant.AutoTest.Toolchains <command> [options]",
            IsHelpCommand = true,
            SubCommands = [discoverCommand, verifyCommand],
        };
        var parser = new CommandParser
        {
            RootCommand = rootCommand,
            DefaultBanner = "Incant toolchains AutoTest",
        };

        try
        {
            Command? invokedCommand = parser.Invoke(arguments);
            if (invokedCommand is null || selectedCommand is null)
            {
                return new AutoTestParseResult(
                    Command: null,
                    ExitCode: parser.ExitCode == 0 ? 0 : 2);
            }

            return new AutoTestParseResult(selectedCommand, parser.ExitCode);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return new AutoTestParseResult(Command: null, ExitCode: 2);
        }
    }

    private static Command CreateDiscoverCommand(Action<AutoTestCommand> selectCommand)
    {
        var kind = new NullableEnumOption<Kind>
        {
            Name = "kind",
            ShortName = 'k',
            Help = "Limit discovery to one toolchain family.",
        };
        var explicitRoot = CreateOptionalStringOption(
            "explicit-root",
            "Require discovery of this installation root.");
        var includePreview = CreateFlagOption(
            "include-preview",
            "Accept preview and experimental installations.");
        var json = CreateOptionalStringOption("json", "Write a JSON discovery report to this path.");

        return new Command
        {
            Name = "discover",
            ShortName = 'd',
            Help = "Print the toolchains, SDKs, profiles, and diagnostics found on this host.",
            Usage = "Incant.AutoTest.Toolchains discover [options]",
            Options = [kind, explicitRoot, includePreview, json],
            Execute = () =>
            {
                if (explicitRoot.Value is not null && kind.Value is null)
                {
                    throw new ArgumentException("--explicit-root requires --kind.");
                }

                selectCommand(new AutoTestCommand
                {
                    Operation = AutoTestOperation.Discover,
                    Kind = kind.Value,
                    ExplicitRoot = NormalizeOptionalPath(explicitRoot.Value),
                    IncludePreview = includePreview.Value,
                    JsonPath = json.Value,
                });
                return 0;
            },
        };
    }

    private static Command CreateVerifyCommand(Action<AutoTestCommand> selectCommand)
    {
        var kind = new EnumOption<Kind>
        {
            Name = "kind",
            ShortName = 'k',
            Help = "The toolchain or SDK family that must be verified.",
        };
        var target = new NullableEnumOption<TargetPlatform>
        {
            Name = "target",
            ShortName = 't',
            Help = "The target platform that must resolve.",
        };
        var architecture = new NullableEnumOption<TargetArchitecture>
        {
            Name = "arch",
            ShortName = 'a',
            Help = "The target architecture that must resolve.",
        };
        var productMajor = CreateOptionalMajorOption(
            "product-major",
            "Require this product major version.");
        var compilerMajor = CreateOptionalMajorOption(
            "compiler-major",
            "Require this compiler major version.");
        var sdkMajor = CreateOptionalMajorOption("sdk-major", "Require this SDK major version.");
        var components = new EnumListOption<ComponentKind>
        {
            Name = "component",
            ShortName = 'c',
            Help = "Require one component; repeat the option to require several components.",
            IsRequired = false,
        };
        var minimum = new PositiveIntOption
        {
            Name = "minimum",
            ShortName = 'm',
            Help = "Require at least this many matching installations.",
            IsRequired = false,
            Value = 1,
        };
        var explicitRoot = CreateOptionalStringOption(
            "explicit-root",
            "Also verify discovery through this explicit installation root.");
        var includePreview = CreateFlagOption(
            "include-preview",
            "Accept preview and experimental installations.");
        var json = CreateOptionalStringOption("json", "Write a JSON verification report to this path.");

        return new Command
        {
            Name = "verify",
            ShortName = 'v',
            Help = "Resolve a requested profile and compile C and C++ HelloWorld programs.",
            Usage = "Incant.AutoTest.Toolchains verify --kind <kind> [options]",
            SubCommands = [CreateClangClCommand(selectCommand)],
            Options =
            [
                kind,
                target,
                architecture,
                productMajor,
                compilerMajor,
                sdkMajor,
                components,
                minimum,
                explicitRoot,
                includePreview,
                json,
            ],
            Execute = () =>
            {
                selectCommand(new AutoTestCommand
                {
                    Operation = AutoTestOperation.Verify,
                    Kind = kind.Value,
                    Target = target.Value,
                    Architecture = architecture.Value,
                    ProductMajor = productMajor.Value,
                    CompilerMajor = compilerMajor.Value,
                    SdkMajor = sdkMajor.Value,
                    MinimumCount = minimum.Value,
                    RequiredComponents = Array.AsReadOnly(components.Value.ToArray()),
                    ExplicitRoot = NormalizeOptionalPath(explicitRoot.Value),
                    IncludePreview = includePreview.Value,
                    JsonPath = json.Value,
                });
                return 0;
            },
        };
    }

    private static Command CreateClangClCommand(Action<AutoTestCommand> selectCommand) => new()
    {
        Name = "clang-cl",
        ShortName = 'c',
        Help = "Verify clang-cl with a specific Windows linker implementation.",
        Usage = "Incant.AutoTest.Toolchains verify clang-cl <command> [options]",
        IsHelpCommand = true,
        SubCommands =
        [
            CreateClangClLinkCommand(ClangClLinker.Msvc, selectCommand),
            CreateClangClLinkCommand(ClangClLinker.Lld, selectCommand),
        ],
    };

    private static Command CreateClangClLinkCommand(
        ClangClLinker linker,
        Action<AutoTestCommand> selectCommand)
    {
        var architecture = new EnumOption<TargetArchitecture>
        {
            Name = "arch",
            ShortName = 'a',
            Help = "The Windows target architecture that must compile.",
        };
        var compilerMajor = CreateOptionalMajorOption(
            "compiler-major",
            "Require this clang-cl major version.");
        var msvcMajor = CreateOptionalMajorOption(
            "msvc-major",
            "Require this Visual Studio product major version.");
        var sdkMajor = CreateOptionalMajorOption(
            "sdk-major",
            "Require this Windows SDK major version.");
        var explicitRoot = CreateOptionalStringOption(
            "explicit-root",
            "Also verify LLVM discovery through this explicit installation root.");
        var includePreview = CreateFlagOption(
            "include-preview",
            "Accept preview and experimental installations.");
        var json = CreateOptionalStringOption("json", "Write a JSON verification report to this path.");
        string commandName = linker == ClangClLinker.Msvc ? "msvc-link" : "llvm-link";
        string linkerName = linker == ClangClLinker.Msvc ? "MSVC link.exe" : "LLVM lld-link.exe";

        return new Command
        {
            Name = commandName,
            ShortName = linker == ClangClLinker.Msvc ? 'm' : 'l',
            Help = $"Compile and, when runnable, execute C and C++ HelloWorld programs with clang-cl and {linkerName}.",
            Usage = $"Incant.AutoTest.Toolchains verify clang-cl {commandName} --arch <architecture> [options]",
            Options =
            [
                architecture,
                compilerMajor,
                msvcMajor,
                sdkMajor,
                explicitRoot,
                includePreview,
                json,
            ],
            Execute = () =>
            {
                selectCommand(new AutoTestCommand
                {
                    Operation = AutoTestOperation.VerifyClangCl,
                    Kind = Kind.Llvm,
                    Target = TargetPlatform.Windows,
                    Architecture = architecture.Value,
                    CompilerMajor = compilerMajor.Value,
                    MsvcMajor = msvcMajor.Value,
                    SdkMajor = sdkMajor.Value,
                    RequiredComponents = [ComponentKind.Compiler],
                    ExplicitRoot = NormalizeOptionalPath(explicitRoot.Value),
                    IncludePreview = includePreview.Value,
                    ClangClLinker = linker,
                    JsonPath = json.Value,
                });
                return 0;
            },
        };
    }

    private static NullableStringOption CreateOptionalStringOption(string name, string help) => new()
    {
        Name = name,
        Help = help,
    };

    private static NullableNonNegativeIntOption CreateOptionalMajorOption(string name, string help) => new()
    {
        Name = name,
        Help = help,
    };

    private static FlagOption CreateFlagOption(string name, string help) => new()
    {
        Name = name,
        Help = help,
        IsRequired = false,
    };

    private static string? NormalizeOptionalPath(string? path) =>
        path is null ? null : Path.GetFullPath(path);
}

/// <summary>Parses one required enumeration value through the Base option pipeline.</summary>
internal sealed class EnumOption<TEnum> : Option<TEnum>
    where TEnum : struct, Enum
{
    internal EnumOption()
        : base(default)
    {
    }

    public override string ValueTypeName => typeof(TEnum).Name;

    protected override string FormatValue(TEnum value) => value.ToString();

    protected override bool TryParseValue(string value, out TEnum result) =>
        Enum.TryParse(value, ignoreCase: true, out result) && Enum.IsDefined(result);
}

/// <summary>Parses one optional enumeration value through the Base option pipeline.</summary>
internal sealed class NullableEnumOption<TEnum> : NullableOption<TEnum>
    where TEnum : struct, Enum
{
    public override string ValueTypeName => $"{typeof(TEnum).Name}?";

    protected override string FormatDefinedValue(TEnum value) => value.ToString();

    protected override bool TryParseDefinedValue(string value, out TEnum result) =>
        Enum.TryParse(value, ignoreCase: true, out result) && Enum.IsDefined(result);
}

/// <summary>Accumulates repeated enumeration options in their command-line order.</summary>
internal sealed class EnumListOption<TEnum> : Option<IReadOnlyList<TEnum>>
    where TEnum : struct, Enum
{
    internal EnumListOption()
        : base([])
    {
    }

    public override string ValueTypeName => typeof(TEnum).Name;

    protected override string FormatValue(IReadOnlyList<TEnum> value) =>
        value.Count == 0 ? "[]" : string.Join(',', value);

    protected override bool TryParseValue(string value, out IReadOnlyList<TEnum> result)
    {
        if (!Enum.TryParse(value, ignoreCase: true, out TEnum parsedValue)
            || !Enum.IsDefined(parsedValue))
        {
            result = [];
            return false;
        }

        var values = new TEnum[Value.Count + 1];
        for (int index = 0; index < Value.Count; ++index)
        {
            values[index] = Value[index];
        }

        values[^1] = parsedValue;
        result = Array.AsReadOnly(values);
        return true;
    }
}

/// <summary>Accepts zero and positive major-version numbers.</summary>
internal sealed class NullableNonNegativeIntOption : NullableOption<int>
{
    public override string ValueTypeName => "non-negative integer";

    protected override string FormatDefinedValue(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    protected override bool TryParseDefinedValue(string value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
        && result >= 0;
}

/// <summary>Accepts positive integer values.</summary>
internal sealed class PositiveIntOption : Option<int>
{
    internal PositiveIntOption()
        : base(1)
    {
    }

    public override string ValueTypeName => "positive integer";

    protected override string FormatValue(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    protected override bool TryParseValue(string value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
        && result > 0;
}
