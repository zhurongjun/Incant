using Incant.Base.Cli;

namespace Incant.AutoTest.CppToolchain;

internal static class AutoTestCommandLine
{
    internal static AutoTestParseResult Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        AutoTestOptions? selectedOptions = null;
        Command[] profileCommands = EnvironmentProfiles.All
            .Select(profile => CreateProfileCommand(profile, options => selectedOptions = options))
            .ToArray();
        var parser = new CommandParser
        {
            RootCommand = new Command
            {
                Name = "Incant.AutoTest.CppToolchain",
                Help = "Runs the complete toolchain pipeline for one prepared CI environment.",
                Usage = "Incant.AutoTest.CppToolchain <profile> [options]",
                IsHelpCommand = true,
                SubCommands = profileCommands,
            },
            DefaultBanner = "Incant.AutoTest.CppToolchain",
        };

        try
        {
            Command? invokedCommand = parser.Invoke(arguments);
            if (invokedCommand is null || selectedOptions is null)
            {
                return new AutoTestParseResult(null, parser.ExitCode == 0 ? 0 : 2);
            }

            return new AutoTestParseResult(selectedOptions, parser.ExitCode);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            Console.Error.WriteLine(exception.Message);
            return new AutoTestParseResult(null, 2);
        }
    }

    private static Command CreateProfileCommand(
        EnvironmentProfile profile,
        Action<AutoTestOptions> selectOptions)
    {
        var environment = new NullableStringOption
        {
            Name = "environment",
            Help = "Read the versioned environment manifest from this path.",
        };
        var report = new NullableStringOption
        {
            Name = "report",
            Help = "Write the JSON report to this path.",
        };
        var workRoot = new NullableStringOption
        {
            Name = "work-root",
            Help = "Use this root for build products and process logs.",
        };
        var keepWork = new FlagOption
        {
            Name = "keep-work",
            Help = "Keep successful candidate work directories.",
            IsRequired = false,
        };

        return new Command
        {
            Name = profile.Name,
            Help = profile.Description,
            Usage = $"Incant.AutoTest.CppToolchain {profile.Name} [options]",
            Options = [environment, report, workRoot, keepWork],
            Execute = () =>
            {
                string? environmentPath = environment.Value
                    ?? System.Environment.GetEnvironmentVariable("INCANT_AUTOTEST_ENVIRONMENT");
                selectOptions(new AutoTestOptions(
                    profile,
                    NormalizeOptionalPath(environmentPath),
                    NormalizeReportPath(report.Value ?? Path.Combine(
                        "build", "toolchain-reports", profile.Name + ".json")),
                    NormalizeWorkRoot(workRoot.Value ?? Path.Combine(
                        "build", "toolchain-autotest", profile.Name)),
                    keepWork.Value));
                return 0;
            },
        };
    }

    private static string NormalizeReportPath(string path)
    {
        string result = NormalizePath(path);
        if (string.IsNullOrEmpty(Path.GetFileName(result))
            || Directory.Exists(result))
        {
            throw new ArgumentException(
                $"Report path '{path}' must identify a file.",
                nameof(path));
        }

        return result;
    }

    private static string NormalizeWorkRoot(string path)
    {
        string result = NormalizePath(path);
        if (File.Exists(result))
        {
            throw new ArgumentException(
                $"Work root '{path}' must identify a directory.",
                nameof(path));
        }

        return result;
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : NormalizePath(path);
}
