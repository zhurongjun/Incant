using System.Text.RegularExpressions;
using SB.Core;

namespace SB;

public sealed class RegisteredTestExecutionPlan
{
    public string ModuleName { get; init; } = "";
    public string ModuleDirectory { get; init; } = "";
    public string TestName { get; init; } = "";
    public string TargetName { get; init; } = "";
}

public sealed class RegisteredTestModuleExecutionPlan
{
    public List<RegisteredTestExecutionPlan> Tests { get; init; } = [];

    public static RegisteredTestModuleExecutionPlan FromTargets(IEnumerable<Target> targets, string? module, string? filter)
    {
        return new RegisteredTestModuleExecutionPlan
        {
            Tests = targets
                .Where(RegisteredAssetHelpers.IsCppTestProgram)
                .Select(target => new RegisteredTestExecutionPlan
                {
                    ModuleName = RegisteredAssetHelpers.ModuleNameOf(target),
                    ModuleDirectory = target.Directory,
                    TestName = target.Name,
                    TargetName = target.Name
                })
                .Where(test => RegisteredAssetHelpers.MatchesModule(test.ModuleName, test.ModuleDirectory, module))
                .Where(test => RegisteredAssetHelpers.MatchesFilter(filter, test.ModuleName, test.ModuleDirectory, test.TestName, test.TargetName))
                .OrderBy(test => test.ModuleName)
                .ThenBy(test => test.TestName)
                .ToList()
        };
    }
}

public sealed class RegisteredBenchExecutionPlan
{
    public string ModuleName { get; init; } = "";
    public string ModuleDirectory { get; init; } = "";
    public string BenchName { get; init; } = "";
    public string TargetName { get; init; } = "";
}

public sealed class RegisteredBenchModuleExecutionPlan
{
    public List<RegisteredBenchExecutionPlan> Benches { get; init; } = [];

    public static RegisteredBenchModuleExecutionPlan FromTargets(IEnumerable<Target> targets, string? module, string? filter)
    {
        return new RegisteredBenchModuleExecutionPlan
        {
            Benches = targets
                .Where(RegisteredAssetHelpers.IsCppBenchListProgram)
                .Select(target => new RegisteredBenchExecutionPlan
                {
                    ModuleName = RegisteredAssetHelpers.ModuleNameOf(target),
                    ModuleDirectory = target.Directory,
                    BenchName = target.Name,
                    TargetName = target.Name
                })
                .Where(bench => RegisteredAssetHelpers.MatchesModule(bench.ModuleName, bench.ModuleDirectory, module))
                .Where(bench => RegisteredAssetHelpers.MatchesFilter(filter, bench.ModuleName, bench.ModuleDirectory, bench.BenchName, bench.TargetName))
                .OrderBy(bench => bench.ModuleName)
                .ThenBy(bench => bench.BenchName)
                .ToList()
        };
    }
}

public sealed class BenchListAttribute
{
    public bool Visible { get; init; } = true;
}

public static class BenchListTargetExtensions
{
    public static T SetBenchListVisible<T>(this T @this, bool visible)
        where T : Target
    {
        @this.SetAttribute(new BenchListAttribute { Visible = visible }, true);
        return @this;
    }

    public static T HideFromBenchList<T>(this T @this)
        where T : Target
    {
        return @this.SetBenchListVisible(false);
    }

    public static bool IsBenchListVisible(this Target @this)
    {
        return @this.GetAttribute<BenchListAttribute>()?.Visible ?? true;
    }
}

public static class RegisteredAssetHelpers
{
    public static string ModuleNameOf(Target target)
    {
        return ModuleNameOfDirectory(target.Directory);
    }

    public static string ModuleNameOfDirectory(string directory)
    {
        var directoryName = Path.GetFileName(directory);
        if (directoryName.Equals("tests", StringComparison.OrdinalIgnoreCase)
            || directoryName.Equals("bench", StringComparison.OrdinalIgnoreCase))
        {
            var parentDirectory = Path.GetDirectoryName(directory);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
                return Path.GetFileName(parentDirectory);
        }

        return directoryName;
    }

    public static bool MatchesModule(string moduleName, string moduleDirectory, string? module)
    {
        if (string.IsNullOrWhiteSpace(module))
            return true;

        var relativeDirectory = Path.GetRelativePath(Directory.GetCurrentDirectory(), moduleDirectory).Replace('\\', '/');
        var directorySegments = relativeDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return moduleName.Equals(module, StringComparison.OrdinalIgnoreCase)
            || relativeDirectory.Equals(module, StringComparison.OrdinalIgnoreCase)
            || relativeDirectory.EndsWith($"/{module}", StringComparison.OrdinalIgnoreCase)
            || directorySegments.Any(segment => segment.Equals(module, StringComparison.OrdinalIgnoreCase));
    }

    public static bool MatchesFilter(string? filter, params string[] values)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        try
        {
            var regex = new Regex(filter, RegexOptions.IgnoreCase);
            return values.Any(value => regex.IsMatch(value));
        }
        catch (ArgumentException)
        {
            return values.Any(value => value.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static string ToDisplayPath(string path)
    {
        return Path.GetRelativePath(Directory.GetCurrentDirectory(), path).Replace('\\', '/');
    }

    public static bool IsCppTestProgram(Target target)
    {
        return target is CppTarget &&
               target.HasTag(TargetTags.Test) &&
               target.GetTargetType() == TargetType.Executable;
    }

    public static bool IsCppBenchProgram(Target target)
    {
        return target is CppTarget &&
               target.HasTag(TargetTags.Bench) &&
               target.GetTargetType() == TargetType.Executable;
    }

    public static bool IsCppBenchListProgram(Target target)
    {
        return IsCppBenchProgram(target) &&
               target.IsBenchListVisible();
    }
}
