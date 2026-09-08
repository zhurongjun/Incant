using Incant.CXLegacy;
using Incant.CXLegacy.Arguments;
using Incant.CXLegacy.FindSdk;
using static Incant.AutoTest.CXLegacyToolchain.BuildEnvironment;

namespace Incant.AutoTest.CXLegacyToolchain;

internal static class WindowsLibraryChain
{
    internal static BuildPlan Create(
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
        var builder = new BuildPlanBuilder(workDirectory, environment,
            toolchain.AdapterKind == BuildAdapterKind.Msvc
                ? ResponseFileDialect.Msvc : ResponseFileDialect.LlvmWindows);

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

        string archiveReady = LibraryChainScenario.AddStaticLibrary(
            builder, toolchain, staticC, staticExtra, staticCpp, archive,
            (cpp, source, output) => WindowsBuildAdapter.CompileArguments(toolchain, includes, cpp, source, output));

        builder.Add(
            "compile-main-c",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            WindowsBuildAdapter.CompileArguments(
                toolchain, includes, cpp: false, FixturePaths.MainC, mainC),
            artifacts: [mainC]);
        builder.Add(
            "link-main-c",
            BuildActionPhase.Build,
            toolchain.Linker!.Path,
            WindowsBuildAdapter.LinkArguments(
                toolchain,
                libraries,
                [mainC, archive],
                cExecutable),
            ["compile-main-c", archiveReady],
            [cExecutable]);

        builder.Add(
            "compile-shared",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            WindowsBuildAdapter.CompileArguments(
                toolchain, includes, cpp: true, FixturePaths.SharedCpp, sharedObject),
            artifacts: [sharedObject]);
        builder.Add(
            "link-shared",
            BuildActionPhase.Build,
            toolchain.Linker!.Path,
            WindowsBuildAdapter.LinkArguments(
                toolchain,
                libraries,
                [sharedObject, archive],
                sharedLibrary,
                createDll: true,
                importLibrary),
            ["compile-shared", archiveReady],
            [sharedLibrary, importLibrary]);
        builder.Add(
            "compile-main-cpp",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            WindowsBuildAdapter.CompileArguments(
                toolchain, includes, cpp: true, FixturePaths.MainCpp, mainCpp),
            artifacts: [mainCpp]);
        builder.Add(
            "link-main-cpp",
            BuildActionPhase.Build,
            toolchain.Linker!.Path,
            WindowsBuildAdapter.LinkArguments(
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
}
