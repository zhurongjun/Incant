using Incant.Core.Cpp.Arguments;

namespace Incant.AutoTest.CppToolchain;

internal sealed class WasiBuildAdapter : IBuildAdapter
{
    public BuildPlan CreatePlan(AutoTestContext context, ResolvedToolchain toolchain) =>
        WasiLibraryChain.Create(context, toolchain);

    internal static IReadOnlyList<string> CompileArguments(
        ResolvedToolchain toolchain, bool cpp, string source, string output) =>
        DriverArguments.Generate(toolchain, CppOperation.Compile,
            DriverArguments.CompileConfiguration(toolchain, cpp, false, source, output));

    internal static IReadOnlyList<string> LinkArguments(
        ResolvedToolchain toolchain, IReadOnlyList<string> inputs, string output) =>
        DriverArguments.Generate(toolchain, CppOperation.Link,
            DriverArguments.LinkConfiguration(toolchain, inputs, output));
}
