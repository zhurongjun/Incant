using Incant.Core.Cpp;

namespace Incant.AutoTest.CppToolchain;

internal interface IBuildAdapter
{
    BuildPlan CreatePlan(AutoTestContext context, ResolvedToolchain toolchain);
}

internal static class BuildAdapterFactory
{
    internal static IBuildAdapter Create(ResolvedToolchain toolchain) =>
        toolchain.AdapterKind switch
        {
            BuildAdapterKind.Msvc => new MsvcBuildAdapter(),
            BuildAdapterKind.ClangCl => new ClangClBuildAdapter(),
            BuildAdapterKind.Gnu => new GnuBuildAdapter(),
            BuildAdapterKind.Llvm => new LlvmBuildAdapter(),
            BuildAdapterKind.Apple => new AppleBuildAdapter(),
            BuildAdapterKind.Android => new AndroidBuildAdapter(),
            BuildAdapterKind.Emscripten => new EmscriptenBuildAdapter(),
            BuildAdapterKind.Wasi => new WasiBuildAdapter(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(toolchain), toolchain.AdapterKind, null),
        };
}

internal sealed class BuildPlanBuilder(
    string workDirectory,
    IReadOnlyDictionary<string, string?> environment)
{
    private readonly List<BuildAction> _actions = [];

    internal string WorkDirectory { get; } = workDirectory;

    internal string PathFor(string name) => Path.Combine(WorkDirectory, name);

    internal void Add(
        string id,
        BuildActionPhase phase,
        string executable,
        IEnumerable<string> arguments,
        IEnumerable<string>? dependencies = null,
        IEnumerable<string>? artifacts = null,
        IEnumerable<string>? outputFragments = null,
        TimeSpan? timeout = null)
    {
        _actions.Add(new BuildAction(
            id,
            phase,
            executable,
            arguments.ToArray(),
            WorkDirectory,
            environment,
            (dependencies ?? []).ToArray(),
            (artifacts ?? []).ToArray(),
            (outputFragments ?? []).ToArray(),
            timeout ?? TimeSpan.FromMinutes(2)));
    }

    internal BuildPlan Build()
    {
        string[] duplicateIds = _actions.GroupBy(action => action.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateIds.Length > 0)
        {
            throw new InvalidOperationException(
                $"Build action ids must be unique: {string.Join(", ", duplicateIds)}.");
        }

        if (!_actions.Any(action => action.Phase == BuildActionPhase.Build))
        {
            throw new InvalidOperationException(
                "A build plan must contain at least one build action.");
        }

        bool reachedExecution = false;
        var previousIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (BuildAction action in _actions)
        {
            reachedExecution |= action.Phase == BuildActionPhase.Execute;
            if (reachedExecution && action.Phase == BuildActionPhase.Build)
            {
                throw new InvalidOperationException(
                    $"Build action '{action.Id}' appears after execution has started.");
            }

            string? invalidDependency = action.Dependencies.FirstOrDefault(
                dependency => !previousIds.Contains(dependency));
            if (invalidDependency is not null)
            {
                throw new InvalidOperationException(
                    $"Action '{action.Id}' depends on later or unknown action '{invalidDependency}'.");
            }

            previousIds.Add(action.Id);
        }

        return new BuildPlan
        {
            WorkDirectory = WorkDirectory,
            Actions = _actions.ToArray(),
        };
    }
}

internal abstract class BuildAdapter
{
    protected static BuildPlanBuilder CreateBuilder(
        AutoTestContext context,
        ResolvedToolchain toolchain)
    {
        string workDirectory = AutoTestWorkspace.ResetCaseDirectory(
            context, toolchain.Id);
        return new BuildPlanBuilder(
            workDirectory,
            CreateEnvironment(toolchain, workDirectory));
    }

    protected static IReadOnlyDictionary<string, string?> CreateEnvironment(
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
            toolchain.Linker.Path,
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

    protected static IReadOnlyDictionary<string, string?> AddEnvironmentPaths(
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

    protected static IReadOnlyList<string> DriverCompileArguments(
        ResolvedToolchain toolchain,
        bool cpp,
        bool positionIndependent)
    {
        var arguments = new List<string>();
        arguments.AddRange(TargetArguments(toolchain));
        arguments.AddRange(MultilibArguments(toolchain.Multilib));
        arguments.Add(cpp ? "-std=c++17" : "-std=c11");
        if (positionIndependent)
        {
            arguments.Add("-fPIC");
        }

        arguments.AddRange(["-I", FixturePaths.Root]);
        foreach (string directory in DriverResourceArguments.IncludeDirectories(
            toolchain, cpp))
        {
            arguments.AddRange(["-isystem", directory]);
        }

        foreach (string framework in DriverResourceArguments.FrameworkDirectories(
            toolchain))
        {
            arguments.AddRange(["-F", framework]);
        }

        return arguments;
    }

    protected static IReadOnlyList<string> DriverLinkArguments(
        ResolvedToolchain toolchain)
    {
        var arguments = new List<string>();
        arguments.AddRange(TargetArguments(toolchain));
        arguments.AddRange(MultilibArguments(toolchain.Multilib));
        foreach (string directory in DriverResourceArguments.LinkDirectories(
            toolchain))
        {
            arguments.AddRange(["-L", directory]);
        }

        foreach (string framework in DriverResourceArguments.FrameworkDirectories(
            toolchain))
        {
            arguments.AddRange(["-F", framework]);
        }

        return arguments;
    }

    protected static IReadOnlyList<string> TargetArguments(
        ResolvedToolchain toolchain)
    {
        string? sysroot = toolchain.Sdks
            .OrderByDescending(component => component.Role is "platform" or "bundle")
            .Select(component => component.Layout.SysrootPath)
            .FirstOrDefault(path => path is not null);
        var arguments = new List<string>();
        switch (toolchain.AdapterKind)
        {
            case BuildAdapterKind.Gnu:
                if (sysroot is not null)
                {
                    if (toolchain.TargetPlatform == TargetPlatform.MacOS)
                    {
                        arguments.AddRange(["-isysroot", sysroot]);
                    }
                    else
                    {
                        arguments.Add("--sysroot=" + sysroot);
                    }
                }

                break;
            case BuildAdapterKind.Llvm:
                arguments.Add("--target=" + toolchain.TargetTriple);
                if (sysroot is not null)
                {
                    arguments.Add(toolchain.TargetPlatform == TargetPlatform.MacOS
                        ? "-isysroot"
                        : "--sysroot=" + sysroot);
                    if (toolchain.TargetPlatform == TargetPlatform.MacOS)
                    {
                        arguments.Add(sysroot);
                    }
                }

                break;
            case BuildAdapterKind.Apple:
                arguments.AddRange(["-target", toolchain.TargetTriple]);
                if (sysroot is not null)
                {
                    arguments.AddRange(["-isysroot", sysroot]);
                }

                break;
            case BuildAdapterKind.Android:
                string androidTarget = toolchain.TargetTriple
                    + toolchain.AndroidApi!.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                arguments.Add("--target=" + androidTarget);
                if (sysroot is not null)
                {
                    arguments.Add("--sysroot=" + sysroot);
                }

                break;
            case BuildAdapterKind.Wasi:
                arguments.Add("--target=" + toolchain.TargetTriple);
                if (sysroot is not null)
                {
                    arguments.Add("--sysroot=" + sysroot);
                }

                break;
            case BuildAdapterKind.Emscripten:
                if (sysroot is not null)
                {
                    arguments.Add("--sysroot=" + sysroot);
                }

                break;
        }

        return arguments;
    }

    protected static IReadOnlyList<string> MultilibArguments(string? multilib) =>
        multilib switch
        {
            "32" => ["-m32"],
            "64" => ["-m64"],
            "x32" => ["-mx32"],
            _ => [],
        };

    protected static StringComparer PathComparer => PathIdentity.Comparer;

    private static string PrependPath(string path, string? inherited) =>
        string.IsNullOrWhiteSpace(inherited)
            ? path
            : path + Path.PathSeparator + inherited;
}
