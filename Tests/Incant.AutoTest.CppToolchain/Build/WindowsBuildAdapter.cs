using Incant.Core.Cpp;
using Incant.Core.Cpp.FindSdk;

namespace Incant.AutoTest.CppToolchain;

internal sealed class WindowsBuildAdapter : IBuildAdapter
{
    public BuildPlan CreatePlan(AutoTestContext context, ResolvedToolchain toolchain) =>
        WindowsLibraryChain.Create(context, toolchain);

    internal static IReadOnlyList<string> CompileArguments(
        ResolvedToolchain toolchain,
        IReadOnlyList<string> includes,
        bool cpp,
        string source,
        string output)
    {
        var arguments = new List<string>
        {
            "/nologo",
            "/c",
            cpp ? "/TP" : "/TC",
            cpp ? "/std:c++17" : "/std:c11",
            "/MD",
            "/W4",
        };
        if (cpp)
        {
            arguments.Add("/EHsc");
        }

        if (toolchain.AdapterKind == BuildAdapterKind.ClangCl)
        {
            arguments.Add("--target=" + toolchain.DriverConfiguration.TargetTriple);
        }

        foreach (string include in includes)
        {
            arguments.Add("/I" + include);
        }

        arguments.Add("/Fo" + output);
        arguments.Add(source);
        return arguments;
    }

    internal static IReadOnlyList<string> LinkArguments(
        ResolvedToolchain toolchain,
        IReadOnlyList<string> libraryDirectories,
        IReadOnlyList<string> inputs,
        string output,
        bool createDll = false,
        string? importLibrary = null)
    {
        var arguments = new List<string>
        {
            "/NOLOGO",
            "/MACHINE:" + Machine(toolchain.TargetArchitecture),
            "/OUT:" + output,
        };
        if (createDll)
        {
            arguments.Add("/DLL");
            arguments.Add("/IMPLIB:" + importLibrary);
        }
        else
        {
            arguments.Add("/SUBSYSTEM:CONSOLE");
        }

        foreach (string directory in libraryDirectories)
        {
            arguments.Add("/LIBPATH:" + directory);
        }

        arguments.AddRange(inputs);
        return arguments;
    }

    internal static string Machine(TargetArchitecture architecture) =>
        architecture switch
        {
            TargetArchitecture.X86 => "X86",
            TargetArchitecture.X64 => "X64",
            TargetArchitecture.ARM => "ARM",
            TargetArchitecture.ARM64 => "ARM64",
            _ => throw new ArgumentOutOfRangeException(
                nameof(architecture), architecture, null),
        };
}
