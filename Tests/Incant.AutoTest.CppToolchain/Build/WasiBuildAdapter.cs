using static Incant.AutoTest.CppToolchain.DriverArguments;

namespace Incant.AutoTest.CppToolchain;

internal sealed class WasiBuildAdapter : IBuildAdapter
{
    public BuildPlan CreatePlan(AutoTestContext context, ResolvedToolchain toolchain) =>
        WasiLibraryChain.Create(context, toolchain);

    internal static IReadOnlyList<string> CompileArguments(
        ResolvedToolchain toolchain,
        bool cpp,
        string source,
        string output)
    {
        var arguments = new List<string>(
            DriverCompileArguments(toolchain, cpp, positionIndependent: false));
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
