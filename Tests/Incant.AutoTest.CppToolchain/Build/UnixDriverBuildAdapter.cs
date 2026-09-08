using Incant.Core.Arguments;
using Incant.Core.Cpp;
using Incant.Core.Cpp.Arguments;

namespace Incant.AutoTest.CppToolchain;

internal sealed class UnixDriverBuildAdapter : IBuildAdapter
{
    public BuildPlan CreatePlan(AutoTestContext context, ResolvedToolchain toolchain) =>
        UnixLibraryChain.Create(context, toolchain);

    internal static IReadOnlyList<string> CompileArguments(
        ResolvedToolchain toolchain, bool cpp, string source, string output) =>
        DriverArguments.Generate(toolchain, CppOperation.Compile,
            DriverArguments.CompileConfiguration(toolchain, cpp, true, source, output));

    internal static IReadOnlyList<string> LinkArguments(
        ResolvedToolchain toolchain, IReadOnlyList<string> inputs, string output, bool addRuntimePath = false)
    {
        ArgumentSet arguments = DriverArguments.LinkConfiguration(toolchain, inputs, output);
        if (addRuntimePath)
        {
            arguments = toolchain.TargetPlatform switch
            {
                TargetPlatform.Linux => arguments.WithRpaths(["$ORIGIN"]),
                TargetPlatform.MacOS => arguments.WithRpaths(["@loader_path"]),
                _ => arguments,
            };
        }

        return DriverArguments.Generate(toolchain, CppOperation.Link, arguments);
    }

    internal static IReadOnlyList<string> SharedLinkArguments(
        ResolvedToolchain toolchain, string sharedObject, string archive, string output)
    {
        ArgumentSet arguments = DriverArguments.LinkConfiguration(toolchain, [sharedObject, archive], output)
            .WithOutputKind(CppOutputKind.SharedLibrary);
        arguments = toolchain.TargetPlatform is TargetPlatform.MacOS or TargetPlatform.IOS or TargetPlatform.IOSSimulator
            ? arguments.WithInstallName("@rpath/" + Path.GetFileName(output))
            : arguments.WithSoname(Path.GetFileName(output));
        return DriverArguments.Generate(toolchain, CppOperation.Link, arguments);
    }

    internal static string SharedLibraryName(ResolvedToolchain toolchain) =>
        toolchain.TargetPlatform is TargetPlatform.MacOS or TargetPlatform.IOS or TargetPlatform.IOSSimulator
            ? "libincant_fixture_shared.dylib" : "libincant_fixture_shared.so";

    internal static string ExecutableName(ResolvedToolchain toolchain, string stem) =>
        toolchain.TargetPlatform == TargetPlatform.Windows ? stem + ".exe" : stem;
}
