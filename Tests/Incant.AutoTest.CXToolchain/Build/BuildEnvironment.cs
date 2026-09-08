using Incant.CX;

namespace Incant.AutoTest.CXToolchain;

internal static class BuildEnvironment
{
    internal static BuildPlanBuilder CreateBuilder(
        AutoTestContext context,
        ResolvedToolchain toolchain)
    {
        string workDirectory = AutoTestWorkspace.ResetCaseDirectory(
            context, toolchain.Id);
        return new BuildPlanBuilder(
            workDirectory,
            CreateEnvironment(toolchain, workDirectory));
    }

    internal static IReadOnlyDictionary<string, string?> CreateEnvironment(
        ResolvedToolchain toolchain,
        string workDirectory)
    {
        var environment = new Dictionary<string, string?>(
            toolchain.Environment,
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        string inheritedPath = environment.GetValueOrDefault("PATH")
            ?? System.Environment.GetEnvironmentVariable("PATH")
            ?? string.Empty;
        IEnumerable<string> toolDirectories = new[]
        {
            toolchain.CCompiler.Path,
            toolchain.CppCompiler.Path,
            toolchain.Archiver.Path,
            toolchain.Ranlib?.Path,
            toolchain.Linker?.Path,
            toolchain.RuntimePath,
        }
            .Where(path => path is not null)
            .Select(path => Path.GetDirectoryName(path!)!)
            .Distinct(PathComparer);
        environment["PATH"] = string.Join(
            Path.PathSeparator,
            toolDirectories.Append(inheritedPath));

        if (toolchain.ExecutionMode == ExecutionMode.Native)
        {
            if (toolchain.TargetPlatform == TargetPlatform.Linux)
            {
                environment["LD_LIBRARY_PATH"] = PrependPath(
                    workDirectory, environment.GetValueOrDefault("LD_LIBRARY_PATH"));
            }
            else if (toolchain.TargetPlatform == TargetPlatform.MacOS)
            {
                environment["DYLD_LIBRARY_PATH"] = PrependPath(
                    workDirectory, environment.GetValueOrDefault("DYLD_LIBRARY_PATH"));
            }
        }

        return environment;
    }

    internal static IReadOnlyDictionary<string, string?> AddEnvironmentPaths(
        IReadOnlyDictionary<string, string?> source,
        IEnumerable<string> includeDirectories,
        IEnumerable<string> libraryDirectories)
    {
        var environment = new Dictionary<string, string?>(
            source,
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        environment["INCLUDE"] = string.Join(
            Path.PathSeparator,
            includeDirectories.Distinct(PathComparer));
        environment["LIB"] = string.Join(
            Path.PathSeparator,
            libraryDirectories.Distinct(PathComparer));
        return environment;
    }

    internal static StringComparer PathComparer => PathIdentity.Comparer;

    private static string PrependPath(string path, string? inherited) =>
        string.IsNullOrWhiteSpace(inherited)
            ? path
            : path + Path.PathSeparator + inherited;
}
