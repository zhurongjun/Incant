using static Incant.AutoTest.CXLegacyToolchain.BuildEnvironment;

namespace Incant.AutoTest.CXLegacyToolchain;

internal static class WasiLibraryChain
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
        string cExecutable = builder.PathFor("incant-c-chain.wasm");
        string sharedObject = builder.PathFor("shared.o");
        string mainCpp = builder.PathFor("main_cpp.o");
        string cppExecutable = builder.PathFor("incant-cpp-chain.wasm");

        string archiveReady = LibraryChainScenario.AddStaticLibrary(
            builder, toolchain, staticC, staticExtra, staticCpp, archive,
            (cpp, source, output) => WasiBuildAdapter.CompileArguments(toolchain, cpp, source, output));

        builder.Add(
            "compile-main-c",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            WasiBuildAdapter.CompileArguments(
                toolchain, cpp: false, FixturePaths.MainC, mainC),
            artifacts: [mainC]);
        builder.Add(
            "link-main-c",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            WasiBuildAdapter.LinkArguments(toolchain, [mainC, archive], cExecutable),
            ["compile-main-c", archiveReady],
            [cExecutable]);

        builder.Add(
            "compile-shared-object",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            WasiBuildAdapter.CompileArguments(
                toolchain, cpp: true, FixturePaths.SharedCpp, sharedObject),
            artifacts: [sharedObject]);
        builder.Add(
            "compile-main-cpp",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            WasiBuildAdapter.CompileArguments(
                toolchain, cpp: true, FixturePaths.MainCpp, mainCpp),
            artifacts: [mainCpp]);
        builder.Add(
            "link-main-cpp",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            WasiBuildAdapter.LinkArguments(
                toolchain,
                [mainCpp, sharedObject, archive],
                cppExecutable),
            ["compile-main-cpp", "compile-shared-object", archiveReady],
            [cppExecutable]);

        builder.Add(
            "execute-main-c",
            BuildActionPhase.Execute,
            toolchain.RuntimePath!,
            ["run", cExecutable],
            ["link-main-c"],
            outputFragments: ["INCANT_TOOLCHAIN_C_OK"],
            timeout: TimeSpan.FromSeconds(30));
        builder.Add(
            "execute-main-cpp",
            BuildActionPhase.Execute,
            toolchain.RuntimePath!,
            ["run", cppExecutable],
            ["link-main-cpp"],
            outputFragments: ["INCANT_TOOLCHAIN_CPP_OK"],
            timeout: TimeSpan.FromSeconds(30));
        return builder.Build();
    }
}
