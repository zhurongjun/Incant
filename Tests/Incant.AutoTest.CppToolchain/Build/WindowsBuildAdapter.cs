using Incant.Core.Cpp;
using Incant.Core.Cpp.FindSdk;

namespace Incant.AutoTest.CppToolchain;

internal abstract class WindowsBuildAdapter : BuildAdapter, IBuildAdapter
{
    protected abstract bool UsesClangCl { get; }

    public BuildPlan CreatePlan(
        AutoTestContext context,
        ResolvedToolchain toolchain)
    {
        string workDirectory = AutoTestWorkspace.ResetCaseDirectory(
            context, toolchain.Id);
        string[] includes = toolchain.Resources
            .Where(resource => resource.Purpose is ResourcePurpose.CInclude
                or ResourcePurpose.CppInclude)
            .Select(resource => resource.Path)
            .Distinct(PathComparer)
            .ToArray();
        string[] libraries = toolchain.Resources
            .Where(resource => resource.Purpose is ResourcePurpose.LibraryDirectory
                or ResourcePurpose.RuntimeDirectory)
            .Select(resource => resource.Path)
            .Distinct(PathComparer)
            .ToArray();
        IReadOnlyDictionary<string, string?> environment = AddEnvironmentPaths(
            CreateEnvironment(toolchain, workDirectory), includes, libraries);
        var builder = new BuildPlanBuilder(workDirectory, environment);

        string staticC = builder.PathFor("static_c.obj");
        string staticExtra = builder.PathFor("static_extra.obj");
        string staticCpp = builder.PathFor("static_cpp.obj");
        string archive = builder.PathFor("incant_fixture.lib");
        string mainC = builder.PathFor("main_c.obj");
        string cExecutable = builder.PathFor("incant-c-chain.exe");
        string sharedObject = builder.PathFor("shared.obj");
        string sharedLibrary = builder.PathFor("incant_fixture_shared.dll");
        string importLibrary = builder.PathFor("incant_fixture_shared_import.lib");
        string mainCpp = builder.PathFor("main_cpp.obj");
        string cppExecutable = builder.PathFor("incant-cpp-chain.exe");

        builder.Add(
            "compile-static-c",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            CompileArguments(
                toolchain, includes, cpp: false, FixturePaths.StaticC, staticC),
            artifacts: [staticC]);
        builder.Add(
            "compile-static-extra",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            CompileArguments(toolchain, includes, cpp: false, FixturePaths.StaticExtra, staticExtra),
            artifacts: [staticExtra]);
        builder.Add(
            "compile-static-cpp",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            CompileArguments(
                toolchain, includes, cpp: true, FixturePaths.StaticCpp, staticCpp),
            artifacts: [staticCpp]);

        bool librarian = toolchain.Archiver.Name is "lib" or "llvm-lib";
        IReadOnlyList<string> archiveArguments = librarian
            ? ["/NOLOGO", "/OUT:" + archive, staticC, staticExtra, staticCpp]
            : ["rcs", archive, staticC, staticExtra, staticCpp];
        IReadOnlyList<string> archiveListArguments = librarian
            ? ["/NOLOGO", "/LIST", archive]
            : ["t", archive];
        builder.Add(
            "archive-static",
            BuildActionPhase.Build,
            toolchain.Archiver.Path,
            archiveArguments,
            ["compile-static-c", "compile-static-extra", "compile-static-cpp"],
            [archive]);
        builder.Add(
            "inspect-static",
            BuildActionPhase.Build,
            toolchain.Archiver.Path,
            archiveListArguments,
            ["archive-static"],
            outputFragments: ["static_c.obj", "static_extra.obj", "static_cpp.obj"]);

        builder.Add(
            "compile-main-c",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            CompileArguments(
                toolchain, includes, cpp: false, FixturePaths.MainC, mainC),
            artifacts: [mainC]);
        builder.Add(
            "link-main-c",
            BuildActionPhase.Build,
            toolchain.Linker.Path,
            LinkArguments(
                toolchain,
                libraries,
                [mainC, archive],
                cExecutable),
            ["compile-main-c", "archive-static"],
            [cExecutable]);

        builder.Add(
            "compile-shared",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            CompileArguments(
                toolchain, includes, cpp: true, FixturePaths.SharedCpp, sharedObject),
            artifacts: [sharedObject]);
        builder.Add(
            "link-shared",
            BuildActionPhase.Build,
            toolchain.Linker.Path,
            LinkArguments(
                toolchain,
                libraries,
                [sharedObject, archive],
                sharedLibrary,
                createDll: true,
                importLibrary),
            ["compile-shared", "archive-static"],
            [sharedLibrary, importLibrary]);
        builder.Add(
            "compile-main-cpp",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            CompileArguments(
                toolchain, includes, cpp: true, FixturePaths.MainCpp, mainCpp),
            artifacts: [mainCpp]);
        builder.Add(
            "link-main-cpp",
            BuildActionPhase.Build,
            toolchain.Linker.Path,
            LinkArguments(
                toolchain,
                libraries,
                [mainCpp, importLibrary, archive],
                cppExecutable),
            ["compile-main-cpp", "link-shared"],
            [cppExecutable]);

        if (toolchain.ExecutionMode == ExecutionMode.Native)
        {
            builder.Add(
                "execute-main-c",
                BuildActionPhase.Execute,
                cExecutable,
                [],
                ["link-main-c"],
                outputFragments: ["INCANT_TOOLCHAIN_C_OK"],
                timeout: TimeSpan.FromSeconds(30));
            builder.Add(
                "execute-main-cpp",
                BuildActionPhase.Execute,
                cppExecutable,
                [],
                ["link-main-cpp"],
                outputFragments: ["INCANT_TOOLCHAIN_CPP_OK"],
                timeout: TimeSpan.FromSeconds(30));
        }

        return builder.Build();
    }

    private IReadOnlyList<string> CompileArguments(
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

        if (UsesClangCl)
        {
            arguments.Add("--target=" + toolchain.TargetTriple);
        }

        foreach (string include in includes)
        {
            arguments.Add("/I" + include);
        }

        arguments.Add("/Fo" + output);
        arguments.Add(source);
        return arguments;
    }

    private static IReadOnlyList<string> LinkArguments(
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

    private static string Machine(TargetArchitecture architecture) =>
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

internal sealed class MsvcBuildAdapter : WindowsBuildAdapter
{
    protected override bool UsesClangCl => false;
}

internal sealed class ClangClBuildAdapter : WindowsBuildAdapter
{
    protected override bool UsesClangCl => true;
}
