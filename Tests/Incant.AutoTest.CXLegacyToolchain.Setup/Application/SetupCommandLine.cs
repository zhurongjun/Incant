using Incant.Base.Cli;

namespace Incant.AutoTest.CXLegacyToolchain.Setup;

internal static class SetupCommandLine
{
    internal static SetupParseResult Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        SetupOptions? selectedOptions = null;
        Command[] profileCommands = EnvironmentDefinitions.All
            .Select(profile => CreateProfileCommand(profile, options => selectedOptions = options))
            .ToArray();
        var parser = new CommandParser
        {
            RootCommand = new Command
            {
                Name = "Incant.AutoTest.CXLegacyToolchain.Setup",
                Help = "Prepares one complete C++ toolchain test environment.",
                Usage = "Incant.AutoTest.CXLegacyToolchain.Setup <profile> [options]",
                IsHelpCommand = true,
                SubCommands = profileCommands,
            },
            DefaultBanner = "Incant.AutoTest.CXLegacyToolchain.Setup",
        };

        try
        {
            Command? invokedCommand = parser.Invoke(arguments);
            if (invokedCommand is null || selectedOptions is null)
            {
                return new SetupParseResult(null, parser.ExitCode == 0 ? 0 : 2);
            }

            return new SetupParseResult(selectedOptions, parser.ExitCode);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            Console.Error.WriteLine(exception.Message);
            return new SetupParseResult(null, 2);
        }
    }

    private static Command CreateProfileCommand(
        EnvironmentDefinition profile,
        Action<SetupOptions> selectOptions)
    {
        var workspace = new NullableStringOption
        {
            Name = "workspace",
            Help = "Use this repository workspace.",
        };
        var environment = new NullableStringOption
        {
            Name = "environment",
            Help = "Write the versioned environment manifest to this path.",
        };
        var report = new NullableStringOption
        {
            Name = "report",
            Help = "Write the setup report to this path.",
        };
        var workRoot = new NullableStringOption
        {
            Name = "work-root",
            Help = "Use this root for setup logs and temporary state.",
        };
        var toolchainRoot = new NullableStringOption
        {
            Name = "toolchain-root",
            Help = "Install downloaded toolchains below this root.",
        };

        return new Command
        {
            Name = profile.Name,
            Help = profile.Description,
            Usage = $"Incant.AutoTest.CXLegacyToolchain.Setup {profile.Name} [options]",
            Options = [workspace, environment, report, workRoot, toolchainRoot],
            Execute = () =>
            {
                string workspacePath = NormalizeDirectoryPath(
                    workspace.Value
                        ?? Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
                        ?? Directory.GetCurrentDirectory(),
                    Directory.GetCurrentDirectory(),
                    "Workspace");
                string environmentPath = NormalizeFilePath(
                    environment.Value
                        ?? Path.Combine("build", "toolchain-environments", profile.Name + ".json"),
                    workspacePath,
                    "Environment manifest");
                string reportPath = NormalizeFilePath(
                    report.Value
                        ?? Path.Combine(
                            "build",
                            "toolchain-environments",
                            profile.Name + ".setup.json"),
                    workspacePath,
                    "Setup report");
                if (PathComparer.Equals(environmentPath, reportPath))
                {
                    throw new ArgumentException(
                        "The environment manifest and setup report must use different paths.");
                }

                selectOptions(new SetupOptions(
                    profile,
                    workspacePath,
                    environmentPath,
                    reportPath,
                    NormalizeDirectoryPath(
                        workRoot.Value
                            ?? Path.Combine("build", "toolchain-setup", profile.Name),
                        workspacePath,
                        "Work root"),
                    NormalizeDirectoryPath(
                        toolchainRoot.Value ?? Path.Combine("build", "toolchains"),
                        workspacePath,
                        "Toolchain root")));
                return 0;
            },
        };
    }

    private static string NormalizeFilePath(string path, string basePath, string description)
    {
        string result = Path.GetFullPath(path, basePath);
        if (string.IsNullOrEmpty(Path.GetFileName(result)) || Directory.Exists(result))
        {
            throw new ArgumentException($"{description} path '{path}' must identify a file.", nameof(path));
        }

        return result;
    }

    private static string NormalizeDirectoryPath(string path, string basePath, string description)
    {
        string result = Path.GetFullPath(path, basePath);
        if (File.Exists(result))
        {
            throw new ArgumentException($"{description} path '{path}' must identify a directory.", nameof(path));
        }

        return result;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
