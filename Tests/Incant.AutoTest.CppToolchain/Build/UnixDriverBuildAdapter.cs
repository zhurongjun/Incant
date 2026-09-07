using Incant.Core.Cpp;
using static Incant.AutoTest.CppToolchain.DriverArguments;

namespace Incant.AutoTest.CppToolchain;

internal sealed class UnixDriverBuildAdapter : IBuildAdapter
{
    public BuildPlan CreatePlan(AutoTestContext context, ResolvedToolchain toolchain) =>
        UnixLibraryChain.Create(context, toolchain);

    internal static IReadOnlyList<string> CompileArguments(
        ResolvedToolchain toolchain,
        bool cpp,
        string source,
        string output)
    {
        var arguments = new List<string>(
            DriverCompileArguments(toolchain, cpp, positionIndependent: true));
        arguments.AddRange(["-c", source, "-o", output]);
        return arguments;
    }

    internal static IReadOnlyList<string> LinkArguments(
        ResolvedToolchain toolchain,
        IReadOnlyList<string> inputs,
        string output,
        bool addRuntimePath = false)
    {
        var arguments = new List<string>(DriverLinkArguments(toolchain));
        arguments.AddRange(inputs);
        if (addRuntimePath)
        {
            if (toolchain.TargetPlatform == TargetPlatform.Linux)
            {
                arguments.Add("-Wl,-rpath,$ORIGIN");
            }
            else if (toolchain.TargetPlatform == TargetPlatform.MacOS)
            {
                arguments.Add("-Wl,-rpath,@loader_path");
            }
        }

        arguments.AddRange(["-o", output]);
        return arguments;
    }

    internal static IReadOnlyList<string> SharedLinkArguments(
        ResolvedToolchain toolchain,
        string sharedObject,
        string archive,
        string output)
    {
        var arguments = new List<string>(DriverLinkArguments(toolchain));
        if (toolchain.TargetPlatform is TargetPlatform.MacOS
            or TargetPlatform.IOS
            or TargetPlatform.IOSSimulator)
        {
            arguments.Add("-dynamiclib");
            arguments.Add("-Wl,-install_name,@rpath/" + Path.GetFileName(output));
        }
        else
        {
            arguments.Add("-shared");
            arguments.Add("-Wl,-soname," + Path.GetFileName(output));
        }

        arguments.AddRange([sharedObject, archive, "-o", output]);
        return arguments;
    }

    internal static string SharedLibraryName(ResolvedToolchain toolchain) =>
        toolchain.TargetPlatform is TargetPlatform.MacOS
            or TargetPlatform.IOS
            or TargetPlatform.IOSSimulator
            ? "libincant_fixture_shared.dylib"
            : "libincant_fixture_shared.so";

    internal static string ExecutableName(
        ResolvedToolchain toolchain,
        string stem) => toolchain.TargetPlatform == TargetPlatform.Windows
            ? stem + ".exe"
            : stem;
}
