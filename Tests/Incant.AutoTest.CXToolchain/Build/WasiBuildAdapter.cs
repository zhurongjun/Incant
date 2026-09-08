using Incant.CX.Arguments;

namespace Incant.AutoTest.CXToolchain;

internal sealed class WasiBuildAdapter : IBuildAdapter
{
    public BuildPlan CreatePlan(AutoTestContext context, ResolvedToolchain toolchain) =>
        WasiLibraryChain.Create(context, toolchain);

    internal static IReadOnlyList<string> CompileArguments(
        ResolvedToolchain toolchain, bool cpp, string source, string output) =>
        DriverArguments.Generate(toolchain, Operation.Compile,
            DriverArguments.CompileConfiguration(toolchain, cpp, false, source, output));

    internal static IReadOnlyList<string> LinkArguments(
        ResolvedToolchain toolchain, IReadOnlyList<string> inputs, string output) =>
        DriverArguments.Generate(toolchain, Operation.Link,
            DriverArguments.LinkConfiguration(toolchain, inputs, output));
}
