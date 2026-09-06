namespace Incant.AutoTest.CppToolchain;

internal sealed class EmscriptenBuildAdapter : BuildAdapter, IBuildAdapter
{
    public BuildPlan CreatePlan(
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

        builder.Add(
            "compile-static-c",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            CompileArguments(
                toolchain, cpp: false, isPic, FixturePaths.StaticC, staticC),
            artifacts: [staticC]);
        builder.Add(
            "compile-static-extra",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            CompileArguments(toolchain, cpp: false, isPic, FixturePaths.StaticExtra, staticExtra),
            artifacts: [staticExtra]);
        builder.Add(
            "compile-static-cpp",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            CompileArguments(
                toolchain, cpp: true, isPic, FixturePaths.StaticCpp, staticCpp),
            artifacts: [staticCpp]);
        builder.Add(
            "archive-static",
            BuildActionPhase.Build,
            toolchain.Archiver.Path,
            ["rcs", archive, staticC, staticExtra, staticCpp],
            ["compile-static-c", "compile-static-extra", "compile-static-cpp"],
            [archive]);
        builder.Add(
            "index-static",
            BuildActionPhase.Build,
            toolchain.Ranlib!.Path,
            [archive],
            ["archive-static"],
            [archive]);
        builder.Add(
            "inspect-static",
            BuildActionPhase.Build,
            toolchain.Archiver.Path,
            ["t", archive],
            ["index-static"],
            outputFragments: ["static_c.o", "static_extra.o", "static_cpp.o"]);

        builder.Add(
            "compile-main-c",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            CompileArguments(
                toolchain, cpp: false, isPic, FixturePaths.MainC, mainC),
            artifacts: [mainC]);
        builder.Add(
            "link-main-c",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            LinkArguments(
                toolchain,
                [mainC, archive, "-sENVIRONMENT=node"],
                cJavaScript),
            ["compile-main-c", "index-static"],
            [cJavaScript, cWebAssembly]);

        builder.Add(
            "compile-shared",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            CompileArguments(
                toolchain, cpp: true, isPic, FixturePaths.SharedCpp, sharedObject),
            artifacts: [sharedObject]);
        builder.Add(
            "compile-main-cpp",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            CompileArguments(
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
                LinkArguments(
                    toolchain,
                    [sharedObject, archive, "-sSIDE_MODULE=1"],
                    sideModule),
                ["compile-shared", "index-static"],
                [sideModule]);
            builder.Add(
                "link-main-cpp",
                BuildActionPhase.Build,
                toolchain.CppCompiler.Path,
                LinkArguments(
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
                LinkArguments(
                    toolchain,
                    [
                        mainCpp,
                        sharedObject,
                        archive,
                        "-sENVIRONMENT=node",
                    ],
                    cppJavaScript),
                ["compile-main-cpp", "compile-shared", "index-static"],
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

    private static IReadOnlyList<string> CompileArguments(
        ResolvedToolchain toolchain,
        bool cpp,
        bool isPic,
        string source,
        string output)
    {
        var arguments = new List<string>(
            DriverCompileArguments(toolchain, cpp, isPic));
        arguments.AddRange(["-c", source, "-o", output]);
        return arguments;
    }

    private static IReadOnlyList<string> LinkArguments(
        ResolvedToolchain toolchain,
        IReadOnlyList<string> inputs,
        string output)
    {
        var arguments = new List<string>(DriverLinkArguments(toolchain));
        arguments.AddRange(inputs);
        arguments.AddRange(["-o", output]);
        return arguments;
    }
}
