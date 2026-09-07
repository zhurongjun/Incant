using static Incant.AutoTest.CppToolchain.DriverArguments;

namespace Incant.AutoTest.CppToolchain;

internal sealed class EmscriptenBuildAdapter : IBuildAdapter
{
    public BuildPlan CreatePlan(AutoTestContext context, ResolvedToolchain toolchain)
    {
        BuildPlan plan = EmscriptenLibraryChain.Create(context, toolchain);
        return new BuildPlan
        {
            WorkDirectory = plan.WorkDirectory,
            Actions = plan.Actions.Select(action =>
            {
                if (action.Phase == BuildActionPhase.Execute)
                {
                    return action;
                }

                (string executable, IReadOnlyList<string> arguments) =
                    EmscriptenLauncher.ResolveWrapper(context, toolchain, action);
                return action with { ExecutablePath = executable, Arguments = arguments };
            }).ToArray(),
        };
    }

    internal static IReadOnlyList<string> CompileArguments(
        ResolvedToolchain toolchain,
        bool cpp,
        bool isPic,
        string source,
        string output)
    {
        var arguments = new List<string>(
            DriverCompileArguments(toolchain, cpp, isPic));
        arguments.AddRange(["-c", source, "-o", output]);
        return arguments;
    }

    internal static IReadOnlyList<string> LinkArguments(
        ResolvedToolchain toolchain,
        IReadOnlyList<string> inputs,
        string output)
    {
        var arguments = new List<string>(DriverLinkArguments(toolchain));
        arguments.AddRange(inputs);
        arguments.AddRange(["-o", output]);
        return arguments;
    }
}
