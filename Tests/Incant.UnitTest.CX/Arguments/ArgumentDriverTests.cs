using Incant.CX;
using Incant.CX.Arguments;

namespace Incant.UnitTest.CX.Arguments;

public sealed class ArgumentDriverTests
{
    [Fact]
    public void ClangClFileOperandsLeaveTrailingOptionsAvailable()
    {
        ArgumentSet settings = new ArgumentSet().WithInputs(["/Users/example/source file.cpp"])
            .WithOutput("object.obj").WithRaw([new RawArgument("/WX", RawPosition.AfterInputs)]);
        ArgumentGenerationResult result = new ArgumentDriver(Dialect.ClangCl, Operation.Compile).Generate(settings);
        Assert.True(result.Success);
        Assert.Contains("/Tp/Users/example/source file.cpp", result.Arguments);
        Assert.DoesNotContain("--", result.Arguments);
        Assert.Equal("/WX", result.Arguments[^1]);
    }

    [Fact]
    public void NestedLinkGroupsPreserveTheEnclosingWholeArchiveRegion()
    {
        ArgumentSet settings = new ArgumentSet().WithOutput("program").WithLinkInputs(
            [LinkInput.WholeArchive(LinkInput.Group(
                LinkInput.WholeArchive(LinkInput.File("first.a")), LinkInput.File("second.a"))),
                LinkInput.File("third.a")]);
        ArgumentGenerationResult result = new ArgumentDriver(Dialect.Gnu, Operation.Link).Generate(settings);
        Assert.True(result.Success);
        Assert.Equal(["--whole-archive", "first.a", "second.a", "--no-whole-archive", "third.a"],
            result.Arguments.Where(value => value.EndsWith(".a", StringComparison.Ordinal)
                || value is "--whole-archive" or "--no-whole-archive"));
        Assert.Throws<ArgumentNullException>(() => LinkInput.Group(null!));
        Assert.Throws<ArgumentNullException>(() => LinkInput.WholeArchive(null!));
    }

    [Theory]
    [InlineData(Dialect.Msvc, LinkerDialect.Msvc)]
    [InlineData(Dialect.ClangCl, LinkerDialect.LldLink)]
    public void WindowsLinkingUsesSharedConfigurationWithoutInterpretingFrontendOnlyFields(
        Dialect dialect, LinkerDialect linker)
    {
        ArgumentSet settings = new ArgumentSet().WithPlatform(TargetPlatform.Windows)
            .WithArchitecture(TargetArchitecture.X64).WithLanguage(Language.Cpp)
            .WithStandard("c++17").WithWindowsRuntime(WindowsRuntime.MD)
            .WithWarnings(WarningLevel.Extra).WithExceptions(ExceptionMode.Native)
            .WithInputs(["source.cpp"]).WithOutput("object.obj");
        if (dialect == Dialect.ClangCl)
        {
            settings = settings.WithTriple("x86_64-pc-windows-msvc");
        }

        ArgumentGenerationResult compilation = new ArgumentDriver(dialect, Operation.Compile).Generate(settings);
        ArgumentGenerationResult linking = new ArgumentDriver(dialect, Operation.Link).Generate(
            settings.WithInputs(["object.obj"]).WithOutput("program.exe").WithLinkerDialect(linker));
        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics.Select(item => item.Field + ": " + item.Message)));
        Assert.True(linking.Success, string.Join("; ", linking.Diagnostics.Select(item => item.Field + ": " + item.Message)));
        Assert.DoesNotContain(linking.Arguments, value => value.StartsWith("--target=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ArchiveDialect.Gnu, ArchiveMode.List)]
    [InlineData(ArchiveDialect.Msvc, ArchiveMode.List)]
    [InlineData(ArchiveDialect.Ranlib, ArchiveMode.Index)]
    public void ArchiveInspectionDoesNotRequireNewMembers(ArchiveDialect dialect, ArchiveMode mode)
    {
        ArgumentSet settings = new ArgumentSet().WithArchiveDialect(dialect).WithArchiveMode(mode)
            .WithInputs([]).WithOutput("archive.a");
        ArgumentGenerationResult result = new ArgumentDriver(Dialect.Clang, Operation.Archive).Generate(settings);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(item => item.Field + ": " + item.Message)));
    }

    [Fact]
    public void InvalidConfigurationValuesAreDiagnosticsRatherThanInterpreterExceptions()
    {
        var compiler = new ArgumentDriver(Dialect.Clang, Operation.Compile);
        ArgumentSet baseline = new ArgumentSet().WithInputs(["source.cpp"]).WithOutput("object.o");
        Assert.Throws<ArgumentNullException>(() => baseline.WithStandard(null!));
        Assert.False(compiler.Generate(baseline.WithLanguage((Language)100)).Success);
        Assert.False(compiler.Generate(baseline.WithRaw([null!])).Success);
        Assert.False(compiler.Generate(baseline.WithDependencies(new Dependencies((DependencyMode)100))).Success);
        Assert.Throws<ArgumentNullException>(() => compiler.Generate(null!));
    }

    [Fact]
    public void DynamicDebugAcceptsProgramDatabaseAndParticipatesInArchiving()
    {
        ArgumentSet settings = new ArgumentSet().WithPlatform(TargetPlatform.Windows)
            .WithArchitecture(TargetArchitecture.X64).WithCompilerVersion(new Version(19, 44))
            .WithDebug(DebugFormat.ProgramDatabase).WithDynamicDebug(true)
            .WithInputs(["source.cpp"]).WithOutput("object.obj");
        Assert.True(new ArgumentDriver(Dialect.Msvc, Operation.Compile).Generate(settings).Success);
        ArgumentGenerationResult archive = new ArgumentDriver(Dialect.Msvc, Operation.Archive)
            .Generate(settings.WithArchiveDialect(ArchiveDialect.Msvc).WithInputs(["object.obj"]).WithOutput("archive.lib"));
        Assert.True(archive.Success);
        Assert.Contains("/dynamicdeopt", archive.Arguments);
    }

    [Fact]
    public void StaticLibCppRequiresExplicitRuntimeArchivesAfterUserInputs()
    {
        var linker = new ArgumentDriver(Dialect.Clang, Operation.Link);
        ArgumentSet settings = new ArgumentSet().WithPlatform(TargetPlatform.Linux)
            .WithInputs(["main.o"]).WithOutput("program").WithStandardLibrary(StandardLibrary.LibCpp)
            .WithRuntimeLinkage(RuntimeLinkage.Static);
        Assert.False(linker.Generate(settings).Success);
        ArgumentGenerationResult result = linker.Generate(settings.WithRuntimeLibraries(
            [LinkInput.Group(LinkInput.File("libc++.a"), LinkInput.File("libc++abi.a"))]));
        Assert.True(result.Success);
        Assert.Equal(["main.o", "libc++.a", "libc++abi.a"], result.Arguments.Where(value => value.EndsWith(".o", StringComparison.Ordinal)
            || value.EndsWith(".a", StringComparison.Ordinal)));
    }

    [Fact]
    public void DefaultAndExplicitRootSysrootRemainDifferent()
    {
        ArgumentSet settings = new ArgumentSet().WithInputs(["source.cpp"]).WithOutput("object.o");
        var compiler = new ArgumentDriver(Dialect.Clang, Operation.Compile);
        Assert.DoesNotContain(compiler.Generate(settings).Arguments, value => value.StartsWith("--sysroot", StringComparison.Ordinal));
        Assert.Contains("--sysroot=/", compiler.Generate(settings.WithSysroot("/")).Arguments);
    }

    [Theory]
    [InlineData(Dialect.Gnu, TargetPlatform.Linux)]
    [InlineData(Dialect.Clang, TargetPlatform.Linux)]
    [InlineData(Dialect.AppleClang, TargetPlatform.MacOS)]
    [InlineData(Dialect.AndroidClang, TargetPlatform.Android)]
    [InlineData(Dialect.WasiClang, TargetPlatform.Wasi)]
    [InlineData(Dialect.Emscripten, TargetPlatform.Emscripten)]
    public void CompileConfigurationPreservesTokenBoundariesAndCanBeReusedForLinking(Dialect dialect, TargetPlatform platform)
    {
        ArgumentSet settings = new ArgumentSet().WithPlatform(platform).WithLanguage(Language.Cpp)
            .WithStandard("c++17").WithIncludes(["headers with spaces"])
            .WithDefines(new Dictionary<string, string?> { ["TEXT"] = "\"two words\"" })
            .WithInputs(["source with spaces.cpp"]).WithOutput("object with spaces.o");
        var compiler = new ArgumentDriver(dialect, Operation.Compile);
        ArgumentGenerationResult compilation = compiler.Generate(settings);
        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics.Select(item => item.Field + ": " + item.Message)));
        Assert.Contains("headers with spaces", compilation.Arguments);
        Assert.Contains("source with spaces.cpp", compilation.Arguments);
        Assert.Contains("-DTEXT=\"two words\"", compilation.Arguments);
        ArgumentGenerationResult linking = new ArgumentDriver(dialect, Operation.Link)
            .Generate(settings.WithInputs(["object with spaces.o"]).WithOutput("program"));
        Assert.True(linking.Success, string.Join("; ", linking.Diagnostics.Select(item => item.Field + ": " + item.Message)));
        Assert.DoesNotContain("headers with spaces", linking.Arguments);
    }

    [Fact]
    public void LinkGroupsKeepDuplicatesAndRawArgumentsKeepTheirDeclaredPlacement()
    {
        LinkInput[] inputs = [LinkInput.File("first.o"), LinkInput.Group(
            LinkInput.Library("a"), LinkInput.Library("b"), LinkInput.Library("a")), LinkInput.File("last.o")];
        ArgumentSet settings = new ArgumentSet().WithLinkInputs(inputs).WithOutput("program")
            .WithRaw([new RawArgument("before options", RawPosition.BeforeOptions),
                new RawArgument("after inputs", RawPosition.AfterInputs)]);
        inputs[0] = LinkInput.File("changed.o");
        ArgumentGenerationResult result = new ArgumentDriver(Dialect.Gnu, Operation.Link).Generate(settings);
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
            .WithInstructionSet(InstructionSet.Avx2);
        ArgumentGenerationResult result = new ArgumentDriver(Dialect.Emscripten, Operation.Compile).Generate(settings);
        Assert.False(result.Success);
        Assert.Empty(result.Arguments);
        ArgumentDiagnostic diagnostic = Assert.Single(result.Diagnostics, item => item.Field == ArgumentField.InstructionSet);
        Assert.Equal("configuration", Assert.Single(diagnostic.Origins).Metadata["source"]);
    }

    [Fact]
    public void UnknownCompilerVersionOnlyBlocksRequestedVersionSensitiveFeatures()
    {
        ArgumentSet settings = new ArgumentSet().WithInputs(["source.cpp"]).WithOutput("object.obj");
        var compiler = new ArgumentDriver(Dialect.Msvc, Operation.Compile);
        Assert.True(compiler.Generate(settings).Success);
        ArgumentGenerationResult dynamicDebug = compiler.Generate(settings.WithDynamicDebug(true));
        Assert.False(dynamicDebug.Success);
        Assert.Contains(dynamicDebug.Diagnostics, item => item.Field == ArgumentField.DynamicDebug);
    }

    [Fact]
    public void OptimizationDoesNotSelectLtoAndArchivesDemandAnExplicitLtoCapability()
    {
        ArgumentSet settings = new ArgumentSet().WithInputs(["source.cpp"]).WithOutput("object.o")
            .WithOptimization(Optimization.Smallest);
        ArgumentGenerationResult result = new ArgumentDriver(Dialect.Gnu, Operation.Compile).Generate(settings);
        Assert.DoesNotContain(result.Arguments, value => value.StartsWith("-flto", StringComparison.Ordinal));
        var archiver = new ArgumentDriver(Dialect.Gnu, Operation.Archive);
        ArgumentSet archive = settings.WithInputs(["object.o"]).WithOutput("archive.a").WithLto(Lto.Full);
        Assert.False(archiver.Generate(archive).Success);
        Assert.True(archiver.Generate(archive.WithArchiveSupportsLto(true)).Success);
    }

    [Fact]
    public void PchCreationRequiresTheDeclaredMicrosoftObjectOutput()
    {
        ArgumentSet settings = new ArgumentSet().WithInputs(["pch.cpp"]).WithOutput("pch.obj")
            .WithPch(new Pch(PchMode.Create, "pch.hpp", "pch.pch"));
        var compiler = new ArgumentDriver(Dialect.Msvc, Operation.Compile);
        Assert.False(compiler.Generate(settings).Success);
        Assert.True(compiler.Generate(settings.WithPch(new Pch(PchMode.Create, "pch.hpp", "pch.pch", "pch.obj"))).Success);
    }

    [Fact]
    public void EmscriptenModuleConfigurationAppliesToBothCompileAndLink()
    {
        ArgumentSet settings = new ArgumentSet().WithInputs(["module.cpp"]).WithOutput("module.o")
            .WithWasmModule(WasmModule.Side).WithThreads(true).WithPositionIndependent(true);
        ArgumentGenerationResult compilation = new ArgumentDriver(Dialect.Emscripten, Operation.Compile).Generate(settings);
        ArgumentGenerationResult linking = new ArgumentDriver(Dialect.Emscripten, Operation.Link)
            .Generate(settings.WithInputs(["module.o"]).WithOutput("module.wasm"));
        Assert.True(compilation.Success);
        Assert.True(linking.Success);
        Assert.Contains("-sSIDE_MODULE=1", compilation.Arguments);
        Assert.Contains("-sSIDE_MODULE=1", linking.Arguments);
    }
}
