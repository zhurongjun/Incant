namespace Incant.Core.Cpp;

/// <summary>Determines which executable image or launcher interpreter satisfies a host query.</summary>
internal static class HostExecutableInspector
{
    private const int MaximumLauncherDepth = 8;

    internal static Task<TargetArchitecture> SelectAsync(
        string path,
        TargetArchitecture? requested,
        DiscoveryContext context,
        bool allowsLaunchers,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TargetArchitecture architecture = Select(
            BinaryImageReader.Read(path),
            requested,
            context.HostArchitecture);
        if (architecture != TargetArchitecture.Unknown || !allowsLaunchers)
        {
            return Task.FromResult(architecture);
        }

        var visited = new HashSet<string>(SearchPaths.Comparer);
        string? interpreter = FindInterpreter(path, context);
        for (int depth = 0; interpreter is not null && depth < MaximumLauncherDepth; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string identity = SearchPaths.Normalize(interpreter);
            if (!visited.Add(identity))
            {
                break;
            }

            architecture = Select(
                BinaryImageReader.Read(interpreter),
                requested,
                context.HostArchitecture);
            if (architecture != TargetArchitecture.Unknown)
            {
                return Task.FromResult(architecture);
            }

            interpreter = FindInterpreter(interpreter, context);
        }

        return Task.FromResult(TargetArchitecture.Unknown);
    }

    private static TargetArchitecture Select(
        IReadOnlyList<TargetArchitecture> architectures,
        TargetArchitecture? requested,
        TargetArchitecture current)
    {
        if (requested is TargetArchitecture required)
        {
            return architectures.Contains(required)
                ? required
                : TargetArchitecture.Unknown;
        }

        if (current != TargetArchitecture.Unknown && architectures.Contains(current))
        {
            return current;
        }

        return architectures.Count == 1
            ? architectures[0]
            : TargetArchitecture.Unknown;
    }

    private static string? FindInterpreter(
        string path,
        DiscoveryContext context)
    {
        string extension = Path.GetExtension(path);
        if (OperatingSystem.IsWindows()
            && (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)))
        {
            return ResolveCommand(
                context.GetEnvironmentVariable("COMSPEC")
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "cmd.exe"),
                context);
        }

        string? shebang = ReadShebang(path);
        if (shebang is not null
            && ResolveShebang(shebang, context) is string shebangInterpreter)
        {
            return shebangInterpreter;
        }

        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string name in new[]
            {
                context.GetEnvironmentVariable("PYTHON"),
                "python3",
                "python",
            }.OfType<string>())
            {
                string? interpreter = ResolveCommand(name, context);
                if (interpreter is not null)
                {
                    return interpreter;
                }
            }
        }

        return null;
    }

    private static string? ResolveShebang(
        string commandLine,
        DiscoveryContext context)
    {
        string[] arguments = SplitArguments(commandLine);
        if (arguments.Length == 0)
        {
            return null;
        }

        string command = arguments[0];
        if (!string.Equals(
            Path.GetFileName(command),
            "env",
            StringComparison.Ordinal))
        {
            return ResolveCommand(command, context);
        }

        int index = 1;
        while (index < arguments.Length)
        {
            if (arguments[index] == "-S")
            {
                index++;
                break;
            }

            if (arguments[index] is "-u" or "--unset" or "-C" or "--chdir")
            {
                index += 2;
                continue;
            }

            if (arguments[index].StartsWith(
                "-",
                StringComparison.Ordinal)
                || IsEnvironmentAssignment(arguments[index]))
            {
                index++;
                continue;
            }

            break;
        }

        return index < arguments.Length
            ? ResolveCommand(arguments[index], context)
            : null;
    }

    private static bool IsEnvironmentAssignment(string value)
    {
        int separator = value.IndexOf('=');
        return separator > 0
            && value[..separator].All(character =>
                character == '_' || char.IsLetterOrDigit(character));
    }

    private static string? ResolveCommand(
        string command,
        DiscoveryContext context)
    {
        string value = command.Trim().Trim('"');
        if (value.Length == 0)
        {
            return null;
        }

        if (Path.IsPathFullyQualified(value))
        {
            return File.Exists(value)
                ? Path.GetFullPath(value)
                : null;
        }

        return SearchPaths.PathDirectories(context)
            .Select(directory => SearchPaths.Executable(
                directory,
                value,
                wrappers: true))
            .FirstOrDefault(path => path is not null);
    }

    private static string? ReadShebang(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            if (stream.Length < 2)
            {
                return null;
            }

            Span<byte> bytes = stackalloc byte[
                (int)Math.Min(stream.Length, 4096)];
            int count = stream.Read(bytes);
            if (count < 2 || bytes[0] != '#' || bytes[1] != '!')
            {
                return null;
            }

            int end = bytes[2..count].IndexOf((byte)'\n');
            int length = end < 0 ? count - 2 : end;
            return System.Text.Encoding.UTF8.GetString(bytes.Slice(2, length))
                .TrimEnd('\r')
                .Trim();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string[] SplitArguments(string commandLine)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';
        foreach (char character in commandLine)
        {
            if (quote == '\0' && character is '\'' or '"')
            {
                quote = character;
            }
            else if (quote == character)
            {
                quote = '\0';
            }
            else if (quote == '\0' && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0)
        {
            arguments.Add(current.ToString());
        }

        return arguments.ToArray();
    }
}
