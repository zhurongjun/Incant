using Incant.Core.Arguments;
using Incant.Core.Cpp.Arguments;

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
                return action with
                {
                    ExecutablePath = executable,
                    Arguments = arguments,
                    ResponseArgumentOffset = arguments.Count - action.Arguments.Count,
                };
            }).ToArray(),
        };
    }

    internal static IReadOnlyList<string> CompileArguments(
        ResolvedToolchain toolchain, bool cpp, bool isPic, string source, string output,
        CppWasmModule module = CppWasmModule.None) =>
        DriverArguments.Generate(toolchain, CppOperation.Compile,
            DriverArguments.CompileConfiguration(toolchain, cpp, isPic, source, output).WithWasmModule(module));

    internal static IReadOnlyList<string> LinkArguments(
        ResolvedToolchain toolchain, IReadOnlyList<string> inputs, string output,
        CppWasmModule module = CppWasmModule.None)
    {
        ArgumentSet arguments = DriverArguments.LinkConfiguration(toolchain, inputs, output).WithWasmModule(module);
        if (module is not (CppWasmModule.Side or CppWasmModule.SideDeadCodeElimination))
        {
            arguments = arguments.WithWasmEnvironment("node");
        }

        return DriverArguments.Generate(toolchain, CppOperation.Link, arguments);
    }
}
