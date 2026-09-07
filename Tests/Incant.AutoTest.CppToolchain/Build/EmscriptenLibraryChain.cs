using static Incant.AutoTest.CppToolchain.BuildEnvironment;

namespace Incant.AutoTest.CppToolchain;

internal static class EmscriptenLibraryChain
{
    internal static BuildPlan Create(
        AutoTestContext context,
        ResolvedToolchain toolchain)
    {
        BuildPlanBuilder builder = CreateBuilder(context, toolchain);
        bool isPic = toolchain.Multilib is not null
            && toolchain.Multilib.Split('/').Contains(
                "pic", StringComparer.OrdinalIgnoreCase);
        string staticC = builder.PathFor("static_c.o");
        string staticExtra = builder.PathFor("static_extra.o");
        string staticCpp = builder.PathFor("static_cpp.o");
        string archive = builder.PathFor("libincant_fixture.a");
        string mainC = builder.PathFor("main_c.o");
        string cJavaScript = builder.PathFor("incant-c-chain.js");
        string cWebAssembly = builder.PathFor("incant-c-chain.wasm");
        string sharedObject = builder.PathFor("shared.o");
        string mainCpp = builder.PathFor("main_cpp.o");
        string cppJavaScript = builder.PathFor("incant-cpp-chain.js");
        string cppWebAssembly = builder.PathFor("incant-cpp-chain.wasm");

        string archiveReady = LibraryChainScenario.AddStaticLibrary(
            builder, toolchain, staticC, staticExtra, staticCpp, archive,
            (cpp, source, output) => EmscriptenBuildAdapter.CompileArguments(toolchain, cpp, isPic, source, output));

        builder.Add(
            "compile-main-c",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            EmscriptenBuildAdapter.CompileArguments(
                toolchain, cpp: false, isPic, FixturePaths.MainC, mainC),
            artifacts: [mainC]);
        builder.Add(
            "link-main-c",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            EmscriptenBuildAdapter.LinkArguments(
                toolchain,
                [mainC, archive, "-sENVIRONMENT=node"],
                cJavaScript),
            ["compile-main-c", archiveReady],
            [cJavaScript, cWebAssembly]);

        builder.Add(
            "compile-shared",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            EmscriptenBuildAdapter.CompileArguments(
                toolchain, cpp: true, isPic, FixturePaths.SharedCpp, sharedObject),
            artifacts: [sharedObject]);
        builder.Add(
            "compile-main-cpp",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            EmscriptenBuildAdapter.CompileArguments(
                toolchain, cpp: true, isPic, FixturePaths.MainCpp, mainCpp),
            artifacts: [mainCpp]);

        string finalDependency;
        if (isPic)
        {
            string sideModule = builder.PathFor("incant_fixture_side.wasm");
            builder.Add(
                "link-side-module",
                BuildActionPhase.Build,
                toolchain.CppCompiler.Path,
                EmscriptenBuildAdapter.LinkArguments(
                    toolchain,
                    [sharedObject, archive, "-sSIDE_MODULE=1"],
                    sideModule),
                ["compile-shared", archiveReady],
                [sideModule]);
            builder.Add(
                "link-main-cpp",
                BuildActionPhase.Build,
                toolchain.CppCompiler.Path,
                EmscriptenBuildAdapter.LinkArguments(
                    toolchain,
                    [
                        mainCpp,
                        sideModule,
                        archive,
                        "-sMAIN_MODULE=1",
                        "-sENVIRONMENT=node",
                    ],
                    cppJavaScript),
                ["compile-main-cpp", "link-side-module"],
                [cppJavaScript, cppWebAssembly]);
            finalDependency = "link-main-cpp";
        }
        else
        {
            builder.Add(
                "link-main-cpp",
                BuildActionPhase.Build,
                toolchain.CppCompiler.Path,
                EmscriptenBuildAdapter.LinkArguments(
                    toolchain,
                    [
                        mainCpp,
                        sharedObject,
                        archive,
                        "-sENVIRONMENT=node",
                    ],
                    cppJavaScript),
                ["compile-main-cpp", "compile-shared", archiveReady],
                [cppJavaScript, cppWebAssembly]);
            finalDependency = "link-main-cpp";
        }

        builder.Add(
            "execute-main-c",
            BuildActionPhase.Execute,
            toolchain.RuntimePath!,
            [cJavaScript],
            ["link-main-c"],
            outputFragments: ["INCANT_TOOLCHAIN_C_OK"],
            timeout: TimeSpan.FromSeconds(30));
        builder.Add(
            "execute-main-cpp",
            BuildActionPhase.Execute,
            toolchain.RuntimePath!,
            [cppJavaScript],
            [finalDependency],
            outputFragments: ["INCANT_TOOLCHAIN_CPP_OK"],
            timeout: TimeSpan.FromSeconds(30));
        return builder.Build();
    }
}
