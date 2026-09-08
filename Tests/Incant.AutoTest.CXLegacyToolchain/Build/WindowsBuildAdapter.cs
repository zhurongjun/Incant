using Incant.CXLegacy.Arguments;

namespace Incant.AutoTest.CXLegacyToolchain;

internal sealed class WindowsBuildAdapter : IBuildAdapter
{
    public BuildPlan CreatePlan(AutoTestContext context, ResolvedToolchain toolchain) =>
        WindowsLibraryChain.Create(context, toolchain);

    internal static IReadOnlyList<string> CompileArguments(
        ResolvedToolchain toolchain, IReadOnlyList<string> includes, bool cpp, string source, string output)
    {
        ArgumentSet arguments = DriverArguments.Configuration(toolchain)
            .WithLanguage(cpp ? Language.Cpp : Language.C)
            .WithStandard(cpp ? "c++17" : "c11")
            .WithWindowsRuntime(WindowsRuntime.MD)
            .WithWarnings(WarningLevel.Extra)
            .WithIncludes(includes).WithOutput(output).WithInputs([source]);
        if (cpp)
        {
            arguments = arguments.WithExceptions(ExceptionMode.Native);
        }

        return DriverArguments.Generate(toolchain, Operation.Compile, arguments);
    }

    internal static IReadOnlyList<string> LinkArguments(
        ResolvedToolchain toolchain, IReadOnlyList<string> libraryDirectories, IReadOnlyList<string> inputs,
        string output, bool createDll = false, string? importLibrary = null)
    {
        ArgumentSet arguments = DriverArguments.Configuration(toolchain)
            .WithLinkerDialect(toolchain.LinkerFlavor == LinkerFlavor.Lld ? LinkerDialect.LldLink : LinkerDialect.Msvc)
            .WithOutputKind(createDll ? OutputKind.SharedLibrary : OutputKind.Executable)
            .WithLibraryDirectories(libraryDirectories).WithInputs(inputs).WithOutput(output);
        if (createDll && importLibrary is not null)
        {
            arguments = arguments.WithImportLibrary(importLibrary);
        }
        else if (!createDll)
        {
            arguments = arguments.WithSubsystem("CONSOLE");
        }

        return DriverArguments.Generate(toolchain, Operation.Link, arguments);
    }
}
