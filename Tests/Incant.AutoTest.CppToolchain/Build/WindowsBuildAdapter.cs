using Incant.Core.Arguments;
using Incant.Core.Cpp.Arguments;

namespace Incant.AutoTest.CppToolchain;

internal sealed class WindowsBuildAdapter : IBuildAdapter
{
    public BuildPlan CreatePlan(AutoTestContext context, ResolvedToolchain toolchain) =>
        WindowsLibraryChain.Create(context, toolchain);

    internal static IReadOnlyList<string> CompileArguments(
        ResolvedToolchain toolchain, IReadOnlyList<string> includes, bool cpp, string source, string output)
    {
        ArgumentSet arguments = DriverArguments.Configuration(toolchain)
            .WithLanguage(cpp ? CppLanguage.Cpp : CppLanguage.C)
            .WithStandard(cpp ? "c++17" : "c11")
            .WithWindowsRuntime(CppWindowsRuntime.MD)
            .WithWarnings(CppWarningLevel.Extra)
            .WithIncludes(includes).WithOutput(output).WithInputs([source]);
        if (cpp)
        {
            arguments = arguments.WithExceptions(CppExceptionMode.Native);
        }

        return DriverArguments.Generate(toolchain, CppOperation.Compile, arguments);
    }

    internal static IReadOnlyList<string> LinkArguments(
        ResolvedToolchain toolchain, IReadOnlyList<string> libraryDirectories, IReadOnlyList<string> inputs,
        string output, bool createDll = false, string? importLibrary = null)
    {
        ArgumentSet arguments = DriverArguments.Configuration(toolchain)
            .WithLinkerDialect(toolchain.LinkerFlavor == LinkerFlavor.Lld ? CppLinkerDialect.LldLink : CppLinkerDialect.Msvc)
            .WithOutputKind(createDll ? CppOutputKind.SharedLibrary : CppOutputKind.Executable)
            .WithLibraryDirectories(libraryDirectories).WithInputs(inputs).WithOutput(output);
        if (createDll && importLibrary is not null)
        {
            arguments = arguments.WithImportLibrary(importLibrary);
        }
        else if (!createDll)
        {
            arguments = arguments.WithSubsystem("CONSOLE");
        }

        return DriverArguments.Generate(toolchain, CppOperation.Link, arguments);
    }
}
