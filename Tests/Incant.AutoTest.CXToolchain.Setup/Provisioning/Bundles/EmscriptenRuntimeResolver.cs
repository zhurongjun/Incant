using System.Text.RegularExpressions;

namespace Incant.AutoTest.CXToolchain.Setup;

internal sealed record EmscriptenInstallation(
    string RootPath,
    string NodePath,
    string NodeVersion,
    string PythonPath,
    string PythonVersion,
    IReadOnlyDictionary<string, string?> Environment);

internal static partial class EmscriptenRuntimeResolver
{
    internal static async Task<EmscriptenInstallation> ResolveAsync(
        SetupContext context,
        string emsdkRoot,
        string expectedVersion,
        string bootstrapPython,
        EmscriptenHostPackage host,
        CancellationToken cancellationToken)
    {
        string emscriptenRoot = SetupPathGuard.RequireDirectory(
            Path.Combine(emsdkRoot, "upstream", "emscripten"),
            $"Emscripten {expectedVersion}");
        string versionFile = SetupPathGuard.RequireFile(
            Path.Combine(emscriptenRoot, "emscripten-version.txt"),
            "Emscripten version metadata");
        string actualVersion = (await File.ReadAllTextAsync(
            versionFile,
            cancellationToken).ConfigureAwait(false))
            .Trim()
            .Replace("\"", string.Empty, StringComparison.Ordinal);
        if (!string.Equals(actualVersion, expectedVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Emscripten at '{emscriptenRoot}' is version '{actualVersion}'; "
                + $"expected '{expectedVersion}'.");
        }

        string emcc = ResolveLauncher(emscriptenRoot, "emcc");
        string emxx = ResolveLauncher(emscriptenRoot, "em++");
        Console.WriteLine($"[emscripten:launcher] cc={emcc} cxx={emxx}");

        string config = SetupPathGuard.RequireFile(
            Path.Combine(emsdkRoot, ".emscripten"),
            "Emscripten config");
        IReadOnlyDictionary<string, string> configuredPrograms =
            await ReadConfiguredProgramsAsync(
                config,
                emsdkRoot,
                cancellationToken).ConfigureAwait(false);

        LocatedProgram node = await FindProgramAsync(
            context,
            configuredPrograms.GetValueOrDefault("NODE_JS"),
            Path.Combine(emsdkRoot, "node"),
            IsNodeName,
            "Emscripten Node",
            "node",
            "24.19.0",
            additionalCandidates: [],
            cancellationToken).ConfigureAwait(false);
        LocatedProgram python = await FindProgramAsync(
            context,
            configuredPrograms.GetValueOrDefault("PYTHON"),
            Path.Combine(emsdkRoot, "python"),
            IsPythonName,
            "Emscripten Python",
            "python",
            host.PythonFile is null ? null : "3.13.3",
            additionalCandidates: [bootstrapPython],
            cancellationToken).ConfigureAwait(false);

        var environment = new Dictionary<string, string?>
        {
            ["EMSDK"] = emsdkRoot,
            ["EM_CONFIG"] = config,
            ["EMSDK_NODE"] = node.Path,
            ["EMSDK_PYTHON"] = python.Path,
            ["PATH"] = JoinPath(
                Path.GetDirectoryName(python.Path),
                emscriptenRoot,
                Path.GetDirectoryName(node.Path),
                Environment.GetEnvironmentVariable("PATH")),
        };
        return new EmscriptenInstallation(
            emscriptenRoot,
            node.Path,
            node.Version,
            python.Path,
            python.Version,
            environment);
    }

    private static string ResolveLauncher(string root, string stem)
    {
        string[] names = OperatingSystem.IsWindows()
            ? [$"{stem}.exe", $"{stem}.bat", $"{stem}.cmd", $"{stem}.py", stem]
            : [stem, $"{stem}.py", $"{stem}.sh"];
        foreach (string name in names)
        {
            string candidate = Path.Combine(root, name);
            if (File.Exists(candidate))
            {
                return SetupPathGuard.ResolveLink(candidate);
            }
        }

        throw new FileNotFoundException(
            $"{stem} launcher was not found below '{root}'. Tried: {string.Join(", ", names)}.");
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadConfiguredProgramsAsync(
        string config,
        string emsdkRoot,
        CancellationToken cancellationToken)
    {
        string contents = await File.ReadAllTextAsync(config, cancellationToken)
            .ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in contents.Split('\n'))
        {
            Match match = ConfigAssignmentRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            string value = DecodePythonString(match.Groups["value"].Value);
            if (value.Length == 0)
            {
                continue;
            }

            value = Environment.ExpandEnvironmentVariables(value);
            result[match.Groups["key"].Value] = Path.IsPathFullyQualified(value)
                ? Path.GetFullPath(value)
                : Path.GetFullPath(value, emsdkRoot);
        }

        return result;
    }

    private static async Task<LocatedProgram> FindProgramAsync(
        SetupContext context,
        string? preferredPath,
        string searchRoot,
        Func<string, bool> namePredicate,
        string description,
        string program,
        string? expectedVersion,
        IReadOnlyList<string> additionalCandidates,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        AddCandidate(preferredPath);
        if (Directory.Exists(searchRoot))
        {
            foreach (string candidate in EnumeratePrograms(searchRoot, namePredicate))
            {
                AddCandidate(candidate);
            }
        }

        foreach (string candidate in additionalCandidates)
        {
            AddCandidate(candidate);
        }

        var inspected = new List<string>();
        foreach (string candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string version = await ProgramLocator.GetVersionAsync(
                    context,
                    candidate,
                    program,
                    cancellationToken).ConfigureAwait(false);
                inspected.Add($"{candidate} ({version})");
                if (expectedVersion is null
                    || string.Equals(version, expectedVersion, StringComparison.Ordinal))
                {
                    return new LocatedProgram(candidate, version);
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
                or NotSupportedException)
            {
                inspected.Add($"{candidate} (unusable: {exception.Message})");
            }
        }

        string inspectedText = inspected.Count == 0 ? "none" : string.Join("; ", inspected);
        throw new FileNotFoundException(
            expectedVersion is null
                ? $"{description} was not found. Inspected: {inspectedText}."
                : $"{description} {expectedVersion} was not found. Inspected: {inspectedText}.");

        void AddCandidate(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
            {
                return;
            }

            string resolved = SetupPathGuard.ResolveLink(candidate);
            if (seen.Add(resolved))
            {
                candidates.Add(resolved);
            }
        }
    }

    private static IEnumerable<string> EnumeratePrograms(
        string root,
        Func<string, bool> namePredicate)
    {
        var directories = new Queue<string>();
        var visited = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        directories.Enqueue(Path.GetFullPath(root));
        while (directories.TryDequeue(out string? directory))
        {
            string resolvedDirectory = SetupPathGuard.ResolveLink(directory);
            if (!visited.Add(resolvedDirectory))
            {
                continue;
            }

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(entries, StringComparer.Ordinal);
            foreach (string entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    directories.Enqueue(entry);
                }
                else if (File.Exists(entry) && namePredicate(Path.GetFileName(entry)))
                {
                    yield return entry;
                }
            }
        }
    }

    private static bool IsNodeName(string name) =>
        string.Equals(
            name,
            OperatingSystem.IsWindows() ? "node.exe" : "node",
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool IsPythonName(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return string.Equals(name, "python.exe", StringComparison.OrdinalIgnoreCase);
        }

        return PythonProgramRegex().IsMatch(name);
    }

    private static string DecodePythonString(string value) =>
        value
            .Replace(@"\\", @"\", StringComparison.Ordinal)
            .Replace(@"\'", "'", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal);

    private static string JoinPath(params string?[] paths)
    {
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return string.Join(
            Path.PathSeparator,
            paths.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .Where(seen.Add));
    }

    [GeneratedRegex(
        @"^\s*(?<key>NODE_JS|PYTHON)\s*=\s*(?:\[\s*)?[rRuU]*(?<quote>['""])(?<value>.*?)(?:\k<quote>)")]
    private static partial Regex ConfigAssignmentRegex();

    [GeneratedRegex(@"^python(?:3(?:\.\d+)*)?$")]
    private static partial Regex PythonProgramRegex();
}
