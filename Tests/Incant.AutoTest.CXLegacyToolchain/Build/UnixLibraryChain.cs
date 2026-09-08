using Incant.CXLegacy;
using static Incant.AutoTest.CXLegacyToolchain.BuildEnvironment;

namespace Incant.AutoTest.CXLegacyToolchain;

internal static class UnixLibraryChain
{
    internal static BuildPlan Create(
        AutoTestContext context,
        ResolvedToolchain toolchain)
    {
        BuildPlanBuilder builder = CreateBuilder(context, toolchain);
        string staticC = builder.PathFor("static_c.o");
        string staticExtra = builder.PathFor("static_extra.o");
        string staticCpp = builder.PathFor("static_cpp.o");
        string archive = builder.PathFor("libincant_fixture.a");
        string mainC = builder.PathFor("main_c.o");
        string cExecutable = builder.PathFor(UnixDriverBuildAdapter.ExecutableName(
            toolchain, "incant-c-chain"));
        string sharedObject = builder.PathFor("shared.o");
        string sharedLibrary = builder.PathFor(UnixDriverBuildAdapter.SharedLibraryName(toolchain));
        string mainCpp = builder.PathFor("main_cpp.o");
        string cppExecutable = builder.PathFor(UnixDriverBuildAdapter.ExecutableName(
            toolchain, "incant-cpp-chain"));

        string archiveReady = LibraryChainScenario.AddStaticLibrary(
            builder, toolchain, staticC, staticExtra, staticCpp, archive,
            (cpp, source, output) => UnixDriverBuildAdapter.CompileArguments(toolchain, cpp, source, output));

        builder.Add(
            "compile-main-c",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            UnixDriverBuildAdapter.CompileArguments(
                toolchain, cpp: false, FixturePaths.MainC, mainC),
            artifacts: [mainC]);
        builder.Add(
            "link-main-c",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            UnixDriverBuildAdapter.LinkArguments(toolchain, [mainC, archive], cExecutable),
            ["compile-main-c", archiveReady],
            [cExecutable]);

        builder.Add(
            "compile-shared",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            UnixDriverBuildAdapter.CompileArguments(
                toolchain, cpp: true, FixturePaths.SharedCpp, sharedObject),
            artifacts: [sharedObject]);
        builder.Add(
            "link-shared",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            UnixDriverBuildAdapter.SharedLinkArguments(
                toolchain, sharedObject, archive, sharedLibrary),
            ["compile-shared", archiveReady],
            [sharedLibrary]);
        builder.Add(
            "compile-main-cpp",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            UnixDriverBuildAdapter.CompileArguments(
                toolchain, cpp: true, FixturePaths.MainCpp, mainCpp),
            artifacts: [mainCpp]);
        builder.Add(
            "link-main-cpp",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            UnixDriverBuildAdapter.LinkArguments(
                toolchain,
                [mainCpp, sharedLibrary, archive],
                cppExecutable,
                addRuntimePath: true),
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
