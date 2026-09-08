using Incant.CXLegacy.Arguments;

namespace Incant.AutoTest.CXLegacyToolchain;

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
        WasmModule module = WasmModule.None) =>
        DriverArguments.Generate(toolchain, Operation.Compile,
            DriverArguments.CompileConfiguration(toolchain, cpp, isPic, source, output).WithWasmModule(module));

    internal static IReadOnlyList<string> LinkArguments(
        ResolvedToolchain toolchain, IReadOnlyList<string> inputs, string output,
        WasmModule module = WasmModule.None)
    {
        ArgumentSet arguments = DriverArguments.LinkConfiguration(toolchain, inputs, output).WithWasmModule(module);
        if (module is not (WasmModule.Side or WasmModule.SideDeadCodeElimination))
        {
            arguments = arguments.WithWasmEnvironment("node");
        }

        return DriverArguments.Generate(toolchain, Operation.Link, arguments);
    }
}
