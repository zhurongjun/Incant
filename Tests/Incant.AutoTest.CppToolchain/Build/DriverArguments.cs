using Incant.Core.Arguments;
using Incant.Core.Cpp.Arguments;

namespace Incant.AutoTest.CppToolchain;

internal static class DriverArguments
{
    internal static ArgumentSet Configuration(ResolvedToolchain toolchain)
    {
        ArgumentSet arguments = new ArgumentSet(new Dictionary<string, string>
        {
            ["scenario"] = toolchain.Id,
            ["installation"] = string.Join(",", toolchain.InstallationIds),
        }).WithPlatform(toolchain.TargetPlatform).WithArchitecture(toolchain.TargetArchitecture);
        if (toolchain.ToolSet.CompilerVersion is Version version)
        {
            arguments = arguments.WithCompilerVersion(version);
        }

        if (toolchain.DriverConfiguration.TargetTriple is string triple
            && toolchain.AdapterKind is not (BuildAdapterKind.Gnu or BuildAdapterKind.Msvc or BuildAdapterKind.Emscripten))
        {
            arguments = arguments.WithTriple(triple);
        }

        if (toolchain.DriverConfiguration.SysrootPath is string sysroot
            && toolchain.AdapterKind is not (BuildAdapterKind.Msvc or BuildAdapterKind.ClangCl))
        {
            arguments = arguments.WithSysroot(sysroot);
        }

        if (toolchain.AndroidApi is int api)
        {
            arguments = arguments.WithAndroidApi(api);
        }

        if (toolchain.Multilib is string multilib)
        {
            arguments = arguments.WithMultilib(multilib);
        }

        if (toolchain.AdapterKind == BuildAdapterKind.Wasi)
        {
            arguments = arguments.WithExceptions(toolchain.Multilib == "eh"
                ? CppExceptionMode.Wasm : CppExceptionMode.Disabled);
            if (toolchain.Multilib == "eh")
            {
                arguments = arguments.WithWasmLegacyExceptions(false);
            }
        }

        return arguments;
    }

    internal static ArgumentSet CompileConfiguration(
        ResolvedToolchain toolchain, bool cpp, bool positionIndependent, string source, string output) =>
        Configuration(toolchain)
            .WithLanguage(cpp ? CppLanguage.Cpp : CppLanguage.C)
            .WithStandard(cpp ? "c++17" : "c11")
            .WithPositionIndependent(positionIndependent)
            .WithInputs([source])
            .WithOutput(output)
            .WithIncludes([FixturePaths.Root])
            .WithSystemIncludes(DriverResourceArguments.IncludeDirectories(toolchain, cpp))
            .WithFrameworkDirectories(DriverResourceArguments.FrameworkDirectories(toolchain));

    internal static ArgumentSet LinkConfiguration(
        ResolvedToolchain toolchain, IReadOnlyList<string> inputs, string output) =>
        Configuration(toolchain)
            .WithInputs(inputs)
            .WithOutput(output)
            .WithLibraryDirectories(DriverResourceArguments.LinkDirectories(toolchain))
            .WithFrameworkDirectories(DriverResourceArguments.FrameworkDirectories(toolchain));

    internal static IReadOnlyList<string> Generate(
        ResolvedToolchain toolchain, CppOperation operation, ArgumentSet arguments)
    {
        var driver = new CppArgumentDriver(Dialect(toolchain), operation);
        ArgumentGenerationResult result = driver.Generate(arguments);
        if (!result.Success)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine,
                result.Diagnostics.Select(diagnostic =>
                    diagnostic.Key + ": " + diagnostic.Message + " Sources: "
                    + string.Join("; ", diagnostic.Origins.Select(origin =>
                        origin.SetId + " [" + string.Join(", ", origin.Metadata.Select(pair => pair.Key + "=" + pair.Value)) + "]")))));
        }

        return result.Arguments;
    }

    internal static IReadOnlyList<string> ArchiveArguments(
        ResolvedToolchain toolchain, CppArchiveMode mode, string archive, IReadOnlyList<string>? inputs = null, bool indexer = false) =>
        Generate(toolchain, CppOperation.Archive, Configuration(toolchain)
            .WithArchiveDialect(indexer ? CppArchiveDialect.Ranlib : toolchain.Archiver.Name is "lib" or "llvm-lib"
                ? CppArchiveDialect.Msvc : CppArchiveDialect.Gnu)
            .WithArchiveMode(mode).WithOutput(archive).WithInputs(inputs ?? []));

    private static CppDialect Dialect(ResolvedToolchain toolchain) => toolchain.AdapterKind switch
    {
        BuildAdapterKind.Msvc => CppDialect.Msvc,
        BuildAdapterKind.ClangCl => CppDialect.ClangCl,
        BuildAdapterKind.Gnu => CppDialect.Gnu,
        BuildAdapterKind.Llvm => CppDialect.Clang,
        BuildAdapterKind.Apple => CppDialect.AppleClang,
        BuildAdapterKind.Android => CppDialect.AndroidClang,
        BuildAdapterKind.Emscripten => CppDialect.Emscripten,
        BuildAdapterKind.Wasi => CppDialect.WasiClang,
        _ => throw new ArgumentOutOfRangeException(nameof(toolchain)),
    };
}
