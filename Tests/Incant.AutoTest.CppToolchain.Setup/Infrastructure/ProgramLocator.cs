using System.Globalization;
using System.Text.RegularExpressions;

namespace Incant.AutoTest.CppToolchain.Setup;

internal sealed record LocatedProgram(string Path, string Version);

internal static partial class ProgramLocator
{
    internal static string RequireCommand(
        IReadOnlyList<string> names,
        string description,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        IReadOnlyList<string> matches = FindCommands(names, environment);
        if (matches.Count == 0)
        {
            throw new FileNotFoundException(
                $"{description} was not found. Tried: {string.Join(", ", names)}.");
        }

        return matches[0];
    }

    internal static IReadOnlyList<string> FindCommands(
        IReadOnlyList<string> names,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        var matches = new List<string>();
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string name in names)
        {
            foreach (string candidate in CommandCandidates(name, environment))
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                string resolved = SetupPathGuard.ResolveLink(candidate);
                if (seen.Add(resolved))
                {
                    matches.Add(Path.GetFullPath(candidate));
                }
            }
        }

        return matches;
    }

    internal static async Task<LocatedProgram> ResolveCompilerAsync(
        SetupContext context,
        IReadOnlyList<string> candidates,
        int major,
        string description,
        CancellationToken cancellationToken)
    {
        var inspected = new List<string>();
        foreach (string executable in FindCommands(candidates))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string version = await GetVersionAsync(
                    context, executable, ProgramIdentity(executable), cancellationToken)
                    .ConfigureAwait(false);
                inspected.Add($"{executable} ({version})");
                if (VersionMajor(version) == major)
                {
                    return new LocatedProgram(executable, version);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is SetupCommandException
                or IOException
                or UnauthorizedAccessException
                or FormatException
                or OverflowException
                or NotSupportedException)
            {
                inspected.Add($"{executable} (unusable: {exception.Message})");
            }
        }

        string suffix = inspected.Count == 0
            ? "No candidate executable was found."
            : $"Inspected: {string.Join("; ", inspected)}.";
        throw new FileNotFoundException(
            $"{description} major version {major} was not found. {suffix}");
    }

    internal static async Task<string> GetVersionAsync(
        SetupContext context,
        string executable,
        string program,
        CancellationToken cancellationToken)
    {
        SetupCommandOutput result = await context.Commands.RunAsync(
            executable,
            ["--version"],
            new SetupCommandOptions(Timeout: TimeSpan.FromMinutes(2)),
            cancellationToken).ConfigureAwait(false);
        string output = $"{result.StandardOutput}\n{result.StandardError}".Trim();
        return ParseVersion(output, program, executable);
    }

    internal static string ParseVersion(string output, string program, string source)
    {
        Regex[] patterns = program switch
        {
            "node" => [NodeVersionRegex()],
            "python" => [PythonVersionRegex()],
            "wasmtime" => [WasmtimeVersionRegex()],
            "clang" => [ClangVersionRegex()],
            "gcc" => [GccVersionRegex(), GccSimpleVersionRegex()],
            "generic" => [GenericVersionRegex()],
            _ => throw new ArgumentException($"Unknown version parser '{program}'.", nameof(program)),
        };
        foreach (Regex pattern in patterns)
        {
            Match match = pattern.Match(output);
            if (match.Success)
            {
                return match.Groups["version"].Value;
            }
        }

        throw new FormatException(
            $"Could not parse the {program} version reported by '{source}': {output.Trim()}");
    }

    internal static int CompareVersions(string left, string right)
    {
        int[] leftParts = VersionParts(left);
        int[] rightParts = VersionParts(right);
        int count = Math.Max(leftParts.Length, rightParts.Length);
        for (int index = 0; index < count; ++index)
        {
            int difference = (index < leftParts.Length ? leftParts[index] : 0)
                - (index < rightParts.Length ? rightParts[index] : 0);
            if (difference != 0)
            {
                return difference;
            }
        }

        return 0;
    }

    internal static int VersionMajor(string version) => VersionParts(version).FirstOrDefault();

    internal static string ProgramIdentity(string executable)
    {
        string name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        if (name == "node")
        {
            return "node";
        }

        if (name.StartsWith("python", StringComparison.Ordinal))
        {
            return "python";
        }

        if (name == "wasmtime")
        {
            return "wasmtime";
        }

        if (name.Contains("clang", StringComparison.Ordinal))
        {
            return "clang";
        }

        if (name.Contains("gcc", StringComparison.Ordinal)
            || name.Contains("g++", StringComparison.Ordinal))
        {
            return "gcc";
        }

        return "generic";
    }

    private static IEnumerable<string> CommandCandidates(
        string name,
        IReadOnlyDictionary<string, string?>? environment)
    {
        if (Path.IsPathFullyQualified(name)
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar))
        {
            if (TryGetFullPath(name, out string resolvedName))
            {
                yield return resolvedName;
            }

            yield break;
        }

        string pathValue = EnvironmentValue(environment, "PATH")
            ?? Environment.GetEnvironmentVariable("PATH")
            ?? string.Empty;
        string[] extensions = OperatingSystem.IsWindows()
            ? WindowsExtensions(name, EnvironmentValue(environment, "PATHEXT")
                ?? Environment.GetEnvironmentVariable("PATHEXT"))
            : [string.Empty];
        foreach (string directory in pathValue.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string unquotedDirectory = directory.Trim('"');
            foreach (string extension in extensions)
            {
                if (TryGetFullPath(
                    Path.Combine(unquotedDirectory, name + extension),
                    out string candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static bool TryGetFullPath(string path, out string result)
    {
        try
        {
            result = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            result = string.Empty;
            return false;
        }
    }

    private static string? EnvironmentValue(
        IReadOnlyDictionary<string, string?>? environment,
        string name)
    {
        if (environment is null)
        {
            return null;
        }

        foreach ((string key, string? value) in environment)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static string[] WindowsExtensions(string name, string? pathExtensions)
    {
        if (!string.IsNullOrEmpty(Path.GetExtension(name)))
        {
            return [string.Empty];
        }

        return (pathExtensions ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => extension.ToLowerInvariant())
            .ToArray();
    }

    private static int[] VersionParts(string version)
    {
        Match match = NumericVersionRegex().Match(version);
        return match.Success
            ? match.Value.Split('.').Select(part =>
                int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray()
            : [];
    }

    [GeneratedRegex(@"^\s*v(?<version>\d+(?:\.\d+){1,3})\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex NodeVersionRegex();

    [GeneratedRegex(@"^\s*Python\s+(?<version>\d+(?:\.\d+){1,3})(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PythonVersionRegex();

    [GeneratedRegex(@"^\s*wasmtime\s+v?(?<version>\d+(?:\.\d+){1,3})(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WasmtimeVersionRegex();

    [GeneratedRegex(@"(?:Apple\s+)?clang\s+version\s+(?<version>\d+(?:\.\d+){1,3})(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ClangVersionRegex();

    [GeneratedRegex(@"(?:gcc|g\+\+)(?:[^\r\n]*?)\s+(?<version>\d+(?:\.\d+){1,3})(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex GccVersionRegex();

    [GeneratedRegex(@"^\s*(?:gcc|g\+\+)\s+(?<version>\d+(?:\.\d+){1,3})(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex GccSimpleVersionRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9.])v?(?<version>\d+(?:\.\d+){1,3})(?![A-Za-z0-9.])")]
    private static partial Regex GenericVersionRegex();

    [GeneratedRegex(@"\d+(?:\.\d+)*")]
    private static partial Regex NumericVersionRegex();
}
