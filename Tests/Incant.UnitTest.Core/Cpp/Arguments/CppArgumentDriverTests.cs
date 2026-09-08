using Incant.Core.Arguments;
using Incant.Core.Cpp;
using Incant.Core.Cpp.Arguments;

namespace Incant.UnitTest.Core.Cpp.Arguments;

public sealed class CppArgumentDriverTests
{
    [Fact]
    public void ClangClFileOperandsLeaveTrailingOptionsAvailable()
    {
        ArgumentSet settings = new ArgumentSet().WithInputs(["/Users/example/source file.cpp"])
            .WithOutput("object.obj").WithRaw([new CppRawArgument("/WX", CppRawPosition.AfterInputs)]);
        ArgumentGenerationResult result = new CppArgumentDriver(CppDialect.ClangCl, CppOperation.Compile).Generate(settings);
        Assert.True(result.Success);
        Assert.Contains("/Tp/Users/example/source file.cpp", result.Arguments);
        Assert.DoesNotContain("--", result.Arguments);
        Assert.Equal("/WX", result.Arguments[^1]);
    }

    [Fact]
    public void NestedLinkGroupsPreserveTheEnclosingWholeArchiveRegion()
    {
        ArgumentSet settings = new ArgumentSet().WithOutput("program").WithLinkInputs(
            [CppLinkInput.WholeArchive(CppLinkInput.Group(
                CppLinkInput.WholeArchive(CppLinkInput.File("first.a")), CppLinkInput.File("second.a"))),
                CppLinkInput.File("third.a")]);
        ArgumentGenerationResult result = new CppArgumentDriver(CppDialect.Gnu, CppOperation.Link).Generate(settings);
        Assert.True(result.Success);
        Assert.Equal(["--whole-archive", "first.a", "second.a", "--no-whole-archive", "third.a"],
            result.Arguments.Where(value => value.EndsWith(".a", StringComparison.Ordinal)
                || value is "--whole-archive" or "--no-whole-archive"));
        Assert.Throws<ArgumentNullException>(() => CppLinkInput.Group(null!));
        Assert.Throws<ArgumentNullException>(() => CppLinkInput.WholeArchive(null!));
    }

    [Theory]
    [InlineData(CppDialect.Msvc, CppLinkerDialect.Msvc)]
    [InlineData(CppDialect.ClangCl, CppLinkerDialect.LldLink)]
    public void WindowsLinkingUsesSharedConfigurationWithoutInterpretingFrontendOnlyFields(
        CppDialect dialect, CppLinkerDialect linker)
    {
        ArgumentSet settings = new ArgumentSet().WithPlatform(TargetPlatform.Windows)
            .WithArchitecture(TargetArchitecture.X64).WithLanguage(CppLanguage.Cpp)
            .WithStandard("c++17").WithWindowsRuntime(CppWindowsRuntime.MD)
            .WithWarnings(CppWarningLevel.Extra).WithExceptions(CppExceptionMode.Native)
            .WithInputs(["source.cpp"]).WithOutput("object.obj");
        if (dialect == CppDialect.ClangCl)
        {
            settings = settings.WithTriple("x86_64-pc-windows-msvc");
        }

        ArgumentGenerationResult compilation = new CppArgumentDriver(dialect, CppOperation.Compile).Generate(settings);
        ArgumentGenerationResult linking = new CppArgumentDriver(dialect, CppOperation.Link).Generate(
            settings.WithInputs(["object.obj"]).WithOutput("program.exe").WithLinkerDialect(linker));
        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics.Select(item => item.Key + ": " + item.Message)));
        Assert.True(linking.Success, string.Join("; ", linking.Diagnostics.Select(item => item.Key + ": " + item.Message)));
        Assert.DoesNotContain(linking.Arguments, value => value.StartsWith("--target=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CppArchiveDialect.Gnu, CppArchiveMode.List)]
    [InlineData(CppArchiveDialect.Msvc, CppArchiveMode.List)]
    [InlineData(CppArchiveDialect.Ranlib, CppArchiveMode.Index)]
    public void ArchiveInspectionDoesNotRequireNewMembers(CppArchiveDialect dialect, CppArchiveMode mode)
    {
        ArgumentSet settings = new ArgumentSet().WithArchiveDialect(dialect).WithArchiveMode(mode)
            .WithInputs([]).WithOutput("archive.a");
        ArgumentGenerationResult result = new CppArgumentDriver(CppDialect.Clang, CppOperation.Archive).Generate(settings);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(item => item.Key + ": " + item.Message)));
    }

    [Fact]
    public void InvalidConfigurationValuesAreDiagnosticsRatherThanInterpreterExceptions()
    {
        var compiler = new CppArgumentDriver(CppDialect.Clang, CppOperation.Compile);
        ArgumentSet baseline = new ArgumentSet().WithInputs(["source.cpp"]).WithOutput("object.o");
        Assert.False(compiler.Generate(baseline.WithStandard(null!)).Success);
        Assert.False(compiler.Generate(baseline.WithLanguage((CppLanguage)100)).Success);
        Assert.False(compiler.Generate(baseline.WithRaw([null!])).Success);
        Assert.False(compiler.Generate(baseline.WithDependencies(new CppDependencies((CppDependencyMode)100))).Success);
        Assert.Throws<ArgumentNullException>(() => compiler.Generate(null!));
    }

    [Fact]
    public void DynamicDebugAcceptsProgramDatabaseAndParticipatesInArchiving()
    {
        ArgumentSet settings = new ArgumentSet().WithPlatform(TargetPlatform.Windows)
            .WithArchitecture(TargetArchitecture.X64).WithCompilerVersion(new Version(19, 44))
            .WithDebug(CppDebugFormat.ProgramDatabase).WithDynamicDebug(true)
            .WithInputs(["source.cpp"]).WithOutput("object.obj");
        Assert.True(new CppArgumentDriver(CppDialect.Msvc, CppOperation.Compile).Generate(settings).Success);
        ArgumentGenerationResult archive = new CppArgumentDriver(CppDialect.Msvc, CppOperation.Archive)
            .Generate(settings.WithArchiveDialect(CppArchiveDialect.Msvc).WithInputs(["object.obj"]).WithOutput("archive.lib"));
        Assert.True(archive.Success);
        Assert.Contains("/dynamicdeopt", archive.Arguments);
    }

    [Fact]
    public void StaticLibCppRequiresExplicitRuntimeArchivesAfterUserInputs()
    {
        var linker = new CppArgumentDriver(CppDialect.Clang, CppOperation.Link);
        ArgumentSet settings = new ArgumentSet().WithPlatform(TargetPlatform.Linux)
            .WithInputs(["main.o"]).WithOutput("program").WithStandardLibrary(CppStandardLibrary.LibCpp)
            .WithRuntimeLinkage(CppRuntimeLinkage.Static);
        Assert.False(linker.Generate(settings).Success);
        ArgumentGenerationResult result = linker.Generate(settings.WithRuntimeLibraries(
            [CppLinkInput.Group(CppLinkInput.File("libc++.a"), CppLinkInput.File("libc++abi.a"))]));
        Assert.True(result.Success);
        Assert.Equal(["main.o", "libc++.a", "libc++abi.a"], result.Arguments.Where(value => value.EndsWith(".o", StringComparison.Ordinal)
            || value.EndsWith(".a", StringComparison.Ordinal)));
    }

    [Fact]
    public void DefaultAndExplicitRootSysrootRemainDifferent()
    {
        ArgumentSet settings = new ArgumentSet().WithInputs(["source.cpp"]).WithOutput("object.o");
        var compiler = new CppArgumentDriver(CppDialect.Clang, CppOperation.Compile);
        Assert.DoesNotContain(compiler.Generate(settings).Arguments, value => value.StartsWith("--sysroot", StringComparison.Ordinal));
        Assert.Contains("--sysroot=/", compiler.Generate(settings.WithSysroot("/")).Arguments);
    }

    [Theory]
    [InlineData(CppDialect.Gnu, TargetPlatform.Linux)]
    [InlineData(CppDialect.Clang, TargetPlatform.Linux)]
    [InlineData(CppDialect.AppleClang, TargetPlatform.MacOS)]
    [InlineData(CppDialect.AndroidClang, TargetPlatform.Android)]
    [InlineData(CppDialect.WasiClang, TargetPlatform.Wasi)]
    [InlineData(CppDialect.Emscripten, TargetPlatform.Emscripten)]
    public void CompileConfigurationPreservesTokenBoundariesAndCanBeReusedForLinking(CppDialect dialect, TargetPlatform platform)
    {
        ArgumentSet settings = new ArgumentSet().WithPlatform(platform).WithLanguage(CppLanguage.Cpp)
            .WithStandard("c++17").WithIncludes(["headers with spaces"])
            .WithDefines(new Dictionary<string, string?> { ["TEXT"] = "\"two words\"" })
            .WithInputs(["source with spaces.cpp"]).WithOutput("object with spaces.o");
        var compiler = new CppArgumentDriver(dialect, CppOperation.Compile);
        ArgumentGenerationResult compilation = compiler.Generate(settings);
        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics.Select(item => item.Key + ": " + item.Message)));
        Assert.Contains("headers with spaces", compilation.Arguments);
        Assert.Contains("source with spaces.cpp", compilation.Arguments);
        Assert.Contains("-DTEXT=\"two words\"", compilation.Arguments);
        ArgumentGenerationResult linking = new CppArgumentDriver(dialect, CppOperation.Link)
            .Generate(settings.WithInputs(["object with spaces.o"]).WithOutput("program"));
        Assert.True(linking.Success, string.Join("; ", linking.Diagnostics.Select(item => item.Key + ": " + item.Message)));
        Assert.DoesNotContain("headers with spaces", linking.Arguments);
    }

    [Fact]
    public void LinkGroupsKeepDuplicatesAndRawArgumentsKeepTheirDeclaredPlacement()
    {
        CppLinkInput[] inputs = [CppLinkInput.File("first.o"), CppLinkInput.Group(
            CppLinkInput.Library("a"), CppLinkInput.Library("b"), CppLinkInput.Library("a")), CppLinkInput.File("last.o")];
        ArgumentSet settings = new ArgumentSet().WithLinkInputs(inputs).WithOutput("program")
            .WithRaw([new CppRawArgument("before options", CppRawPosition.BeforeOptions),
                new CppRawArgument("after inputs", CppRawPosition.AfterInputs)]);
        inputs[0] = CppLinkInput.File("changed.o");
        ArgumentGenerationResult result = new CppArgumentDriver(CppDialect.Gnu, CppOperation.Link).Generate(settings);
        Assert.True(result.Success);
        Assert.Equal("before options", result.Arguments[0]);
        Assert.Equal("after inputs", result.Arguments[^1]);
        Assert.Equal(["first.o", "-la", "-lb", "-la", "last.o"], result.Arguments
            .Where(value => value is "first.o" or "last.o" or "-la" or "-lb"));
    }

    [Fact]
    public void UnsupportedExplicitFeatureHasOriginsAndNoPartialCommand()
    {
        ArgumentSet settings = new ArgumentSet(new Dictionary<string, string> { ["source"] = "configuration" })
            .WithInputs(["source.cpp"]).WithOutput("object.o").WithArchitecture(TargetArchitecture.Wasm32)
            .WithInstructionSet(CppInstructionSet.Avx2);
        ArgumentGenerationResult result = new CppArgumentDriver(CppDialect.Emscripten, CppOperation.Compile).Generate(settings);
        Assert.False(result.Success);
        Assert.Empty(result.Arguments);
        ArgumentDiagnostic diagnostic = Assert.Single(result.Diagnostics, item => item.Key == CppArguments.InstructionSet.Id);
        Assert.Equal("configuration", Assert.Single(diagnostic.Origins).Metadata["source"]);
    }

    [Fact]
    public void UnknownCompilerVersionOnlyBlocksRequestedVersionSensitiveFeatures()
    {
        ArgumentSet settings = new ArgumentSet().WithInputs(["source.cpp"]).WithOutput("object.obj");
        var compiler = new CppArgumentDriver(CppDialect.Msvc, CppOperation.Compile);
        Assert.True(compiler.Generate(settings).Success);
        ArgumentGenerationResult dynamicDebug = compiler.Generate(settings.WithDynamicDebug(true));
        Assert.False(dynamicDebug.Success);
        Assert.Contains(dynamicDebug.Diagnostics, item => item.Key == CppArguments.DynamicDebug.Id);
    }

    [Fact]
    public void OptimizationDoesNotSelectLtoAndArchivesDemandAnExplicitLtoCapability()
    {
        ArgumentSet settings = new ArgumentSet().WithInputs(["source.cpp"]).WithOutput("object.o")
            .WithOptimization(CppOptimization.Smallest);
        ArgumentGenerationResult result = new CppArgumentDriver(CppDialect.Gnu, CppOperation.Compile).Generate(settings);
        Assert.DoesNotContain(result.Arguments, value => value.StartsWith("-flto", StringComparison.Ordinal));
        var archiver = new CppArgumentDriver(CppDialect.Gnu, CppOperation.Archive);
        ArgumentSet archive = settings.WithInputs(["object.o"]).WithOutput("archive.a").WithLto(CppLto.Full);
        Assert.False(archiver.Generate(archive).Success);
        Assert.True(archiver.Generate(archive.WithArchiveSupportsLto(true)).Success);
    }

    [Fact]
    public void PchCreationRequiresTheDeclaredMicrosoftObjectOutput()
    {
        ArgumentSet settings = new ArgumentSet().WithInputs(["pch.cpp"]).WithOutput("pch.obj")
            .WithPch(new CppPch(CppPchMode.Create, "pch.hpp", "pch.pch"));
        var compiler = new CppArgumentDriver(CppDialect.Msvc, CppOperation.Compile);
        Assert.False(compiler.Generate(settings).Success);
        Assert.True(compiler.Generate(settings.WithPch(new CppPch(CppPchMode.Create, "pch.hpp", "pch.pch", "pch.obj"))).Success);
    }

    [Fact]
    public void EmscriptenModuleConfigurationAppliesToBothCompileAndLink()
    {
        ArgumentSet settings = new ArgumentSet().WithInputs(["module.cpp"]).WithOutput("module.o")
            .WithWasmModule(CppWasmModule.Side).WithThreads(true).WithPositionIndependent(true);
        ArgumentGenerationResult compilation = new CppArgumentDriver(CppDialect.Emscripten, CppOperation.Compile).Generate(settings);
        ArgumentGenerationResult linking = new CppArgumentDriver(CppDialect.Emscripten, CppOperation.Link)
            .Generate(settings.WithInputs(["module.o"]).WithOutput("module.wasm"));
        Assert.True(compilation.Success);
        Assert.True(linking.Success);
        Assert.Contains("-sSIDE_MODULE=1", compilation.Arguments);
        Assert.Contains("-sSIDE_MODULE=1", linking.Arguments);
    }
}
