using Incant.Core.Cpp;

namespace Incant.AutoTest.CppToolchain;

internal abstract class UnixDriverBuildAdapter : BuildAdapter, IBuildAdapter
{
    public BuildPlan CreatePlan(
        AutoTestContext context,
        ResolvedToolchain toolchain)
    {
        BuildPlanBuilder builder = CreateBuilder(context, toolchain);
        string staticC = builder.PathFor("static_c.o");
        string staticExtra = builder.PathFor("static_extra.o");
        string staticCpp = builder.PathFor("static_cpp.o");
        string archive = builder.PathFor("libincant_fixture.a");
        string mainC = builder.PathFor("main_c.o");
        string cExecutable = builder.PathFor(ExecutableName(
            toolchain, "incant-c-chain"));
        string sharedObject = builder.PathFor("shared.o");
        string sharedLibrary = builder.PathFor(SharedLibraryName(toolchain));
        string mainCpp = builder.PathFor("main_cpp.o");
        string cppExecutable = builder.PathFor(ExecutableName(
            toolchain, "incant-cpp-chain"));

        builder.Add(
            "compile-static-c",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            CompileArguments(
                toolchain, cpp: false, FixturePaths.StaticC, staticC),
            artifacts: [staticC]);
        builder.Add(
            "compile-static-extra",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            CompileArguments(toolchain, cpp: false, FixturePaths.StaticExtra, staticExtra),
            artifacts: [staticExtra]);
        builder.Add(
            "compile-static-cpp",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            CompileArguments(
                toolchain, cpp: true, FixturePaths.StaticCpp, staticCpp),
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
                toolchain, cpp: false, FixturePaths.MainC, mainC),
            artifacts: [mainC]);
        builder.Add(
            "link-main-c",
            BuildActionPhase.Build,
            toolchain.CCompiler.Path,
            LinkArguments(toolchain, [mainC, archive], cExecutable),
            ["compile-main-c", "index-static"],
            [cExecutable]);

        builder.Add(
            "compile-shared",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            CompileArguments(
                toolchain, cpp: true, FixturePaths.SharedCpp, sharedObject),
            artifacts: [sharedObject]);
        builder.Add(
            "link-shared",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            SharedLinkArguments(
                toolchain, sharedObject, archive, sharedLibrary),
            ["compile-shared", "index-static"],
            [sharedLibrary]);
        builder.Add(
            "compile-main-cpp",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            CompileArguments(
                toolchain, cpp: true, FixturePaths.MainCpp, mainCpp),
            artifacts: [mainCpp]);
        builder.Add(
            "link-main-cpp",
            BuildActionPhase.Build,
            toolchain.CppCompiler.Path,
            LinkArguments(
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

    private static IReadOnlyList<string> CompileArguments(
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

    private static IReadOnlyList<string> LinkArguments(
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

    private static IReadOnlyList<string> SharedLinkArguments(
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

    private static string SharedLibraryName(ResolvedToolchain toolchain) =>
        toolchain.TargetPlatform is TargetPlatform.MacOS
            or TargetPlatform.IOS
            or TargetPlatform.IOSSimulator
            ? "libincant_fixture_shared.dylib"
            : "libincant_fixture_shared.so";

    private static string ExecutableName(
        ResolvedToolchain toolchain,
        string stem) => toolchain.TargetPlatform == TargetPlatform.Windows
            ? stem + ".exe"
            : stem;
}

internal sealed class GnuBuildAdapter : UnixDriverBuildAdapter
{
}

internal sealed class LlvmBuildAdapter : UnixDriverBuildAdapter
{
}

internal sealed class AppleBuildAdapter : UnixDriverBuildAdapter
{
}

internal sealed class AndroidBuildAdapter : UnixDriverBuildAdapter
{
}
