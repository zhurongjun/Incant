using Incant.Core.Arguments;

namespace Incant.Core.Cpp.Arguments;

/// <summary>Independent C/C++ configuration keys. Optional features preserve tool defaults; command structure defaults are documented by their keys and enums.</summary>
public static class CppArguments
{
    /// <summary>Gets the source language; omission selects C++. It does not choose an executable.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppLanguage> Language { get; } = ArgumentKeys.Scalar<CppLanguage>("cpp.Language");

    /// <summary>Gets a language-matched standard such as c11, c++17 or gnu++20. Unsupported spellings are diagnosed.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> Standard { get; } = ArgumentKeys.Scalar<string>("cpp.Standard");

    /// <summary>Gets the output platform used for parameter applicability, without discovering SDK resources.</summary>
    [GenerateArgument]
    public static ArgumentKey<TargetPlatform> Platform { get; } = ArgumentKeys.Scalar<TargetPlatform>("cpp.Platform");

    /// <summary>Gets the output architecture used for instruction-set checks and platform command options.</summary>
    [GenerateArgument]
    public static ArgumentKey<TargetArchitecture> Architecture { get; } = ArgumentKeys.Scalar<TargetArchitecture>("cpp.Architecture");

    /// <summary>Gets a confirmed frontend version for version-sensitive features; ordinary operations allow omission.</summary>
    [GenerateArgument]
    public static ArgumentKey<Version> CompilerVersion { get; } = ArgumentKeys.Scalar<Version>("cpp.CompilerVersion");

    /// <summary>Gets an explicit driver target override. Omission preserves the invocation default.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> Triple { get; } = ArgumentKeys.Scalar<string>("cpp.Triple");

    /// <summary>Gets an explicit sysroot override, including a deliberate root slash. Paths are not discovered or canonicalized here.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> Sysroot { get; } = ArgumentKeys.Scalar<string>("cpp.Sysroot");

    /// <summary>Gets a selected layout: GNU 32/64/x32, Emscripten pic, WASI eh, or default dot. It is not an arbitrary compiler flag.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> Multilib { get; } = ArgumentKeys.Scalar<string>("cpp.Multilib");

    /// <summary>Gets the Android API appended to an unversioned Android triple.</summary>
    [GenerateArgument]
    public static ArgumentKey<int> AndroidApi { get; } = ArgumentKeys.Scalar<int>("cpp.AndroidApi");

    /// <summary>Gets an Apple deployment version. Device and simulator platforms retain distinct target arguments.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> DeploymentVersion { get; } = ArgumentKeys.Scalar<string>("cpp.DeploymentVersion");

    /// <summary>Gets the one command output path. Archive inspection uses this field to identify the existing archive.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> Output { get; } = ArgumentKeys.Scalar<string>("cpp.Output");

    /// <summary>Gets the explicit OutputKind setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppOutputKind> OutputKind { get; } = ArgumentKeys.Scalar<CppOutputKind>("cpp.OutputKind");

    /// <summary>Gets the explicit Optimization setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppOptimization> Optimization { get; } = ArgumentKeys.Scalar<CppOptimization>("cpp.Optimization");

    /// <summary>Gets the explicit FloatingPoint setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppFloatingPoint> FloatingPoint { get; } = ArgumentKeys.Scalar<CppFloatingPoint>("cpp.FloatingPoint");

    /// <summary>Gets the explicit Exceptions setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppExceptionMode> Exceptions { get; } = ArgumentKeys.Scalar<CppExceptionMode>("cpp.Exceptions");

    /// <summary>Gets the explicit Rtti setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> Rtti { get; } = ArgumentKeys.Scalar<bool>("cpp.Rtti");

    /// <summary>Gets the explicit PositionIndependent setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> PositionIndependent { get; } = ArgumentKeys.Scalar<bool>("cpp.PositionIndependent");

    /// <summary>Gets the explicit PositionIndependentExecutable setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> PositionIndependentExecutable { get; } = ArgumentKeys.Scalar<bool>("cpp.PositionIndependentExecutable");

    /// <summary>Gets the explicit Threads setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> Threads { get; } = ArgumentKeys.Scalar<bool>("cpp.Threads");

    /// <summary>Gets the explicit FunctionSections setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> FunctionSections { get; } = ArgumentKeys.Scalar<bool>("cpp.FunctionSections");

    /// <summary>Gets the explicit DataSections setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> DataSections { get; } = ArgumentKeys.Scalar<bool>("cpp.DataSections");

    /// <summary>Gets the explicit Visibility setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppVisibility> Visibility { get; } = ArgumentKeys.Scalar<CppVisibility>("cpp.Visibility");

    /// <summary>Gets the explicit Cpu setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> Cpu { get; } = ArgumentKeys.Scalar<string>("cpp.Cpu");

    /// <summary>Gets the explicit InstructionSet setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppInstructionSet> InstructionSet { get; } = ArgumentKeys.Scalar<CppInstructionSet>("cpp.InstructionSet");

    /// <summary>Gets the explicit Warnings setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppWarningLevel> Warnings { get; } = ArgumentKeys.Scalar<CppWarningLevel>("cpp.Warnings");

    /// <summary>Gets the explicit WarningsAsErrors setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> WarningsAsErrors { get; } = ArgumentKeys.Scalar<bool>("cpp.WarningsAsErrors");

    /// <summary>Gets the explicit Debug setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppDebugFormat> Debug { get; } = ArgumentKeys.Scalar<CppDebugFormat>("cpp.Debug");

    /// <summary>Gets a compiler or linker PDB path, interpreted independently for each operation.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> Pdb { get; } = ArgumentKeys.Scalar<string>("cpp.Pdb");

    /// <summary>Gets explicit MSVC Dynamic Debug selection for compile, archive and link; version and incompatibilities are checked.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> DynamicDebug { get; } = ArgumentKeys.Scalar<bool>("cpp.DynamicDebug");

    /// <summary>Gets the explicit Lto setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppLto> Lto { get; } = ArgumentKeys.Scalar<CppLto>("cpp.Lto");

    /// <summary>Gets the explicit StandardLibrary setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppStandardLibrary> StandardLibrary { get; } = ArgumentKeys.Scalar<CppStandardLibrary>("cpp.StandardLibrary");

    /// <summary>Gets the explicit RuntimeLinkage setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppRuntimeLinkage> RuntimeLinkage { get; } = ArgumentKeys.Scalar<CppRuntimeLinkage>("cpp.RuntimeLinkage");

    /// <summary>Gets the explicit WindowsRuntime setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppWindowsRuntime> WindowsRuntime { get; } = ArgumentKeys.Scalar<CppWindowsRuntime>("cpp.WindowsRuntime");

    /// <summary>Gets the explicit ArchiveMode setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppArchiveMode> ArchiveMode { get; } = ArgumentKeys.Scalar<CppArchiveMode>("cpp.ArchiveMode");

    /// <summary>Gets the explicit ArchiveDialect setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppArchiveDialect> ArchiveDialect { get; } = ArgumentKeys.Scalar<CppArchiveDialect>("cpp.ArchiveDialect");

    /// <summary>Gets the caller-confirmed LTO capability of the selected archive/index tool; the driver never discovers a tool.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> ArchiveSupportsLto { get; } = ArgumentKeys.Scalar<bool>("cpp.ArchiveSupportsLto");

    /// <summary>Gets the explicit DeterministicArchive setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> DeterministicArchive { get; } = ArgumentKeys.Scalar<bool>("cpp.DeterministicArchive");

    /// <summary>Gets the explicit LinkerDialect setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppLinkerDialect> LinkerDialect { get; } = ArgumentKeys.Scalar<CppLinkerDialect>("cpp.LinkerDialect");

    /// <summary>Gets the explicit EntryPoint setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> EntryPoint { get; } = ArgumentKeys.Scalar<string>("cpp.EntryPoint");

    /// <summary>Gets the explicit ImportLibrary setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> ImportLibrary { get; } = ArgumentKeys.Scalar<string>("cpp.ImportLibrary");

    /// <summary>Gets the explicit Subsystem setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> Subsystem { get; } = ArgumentKeys.Scalar<string>("cpp.Subsystem");

    /// <summary>Gets ordered Windows manifest input files to embed while linking.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<string>> ManifestInputs { get; } = ArgumentKeys.Sequence<string>("cpp.ManifestInputs", value => value);

    /// <summary>Gets the explicit DisableDefaultLibraries setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> DisableDefaultLibraries { get; } = ArgumentKeys.Scalar<bool>("cpp.DisableDefaultLibraries");

    /// <summary>Gets the explicit Soname setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> Soname { get; } = ArgumentKeys.Scalar<string>("cpp.Soname");

    /// <summary>Gets the explicit InstallName setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> InstallName { get; } = ArgumentKeys.Scalar<string>("cpp.InstallName");

    /// <summary>Gets the explicit BundleLoader setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> BundleLoader { get; } = ArgumentKeys.Scalar<string>("cpp.BundleLoader");

    /// <summary>Gets the explicit UseRunpath setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> UseRunpath { get; } = ArgumentKeys.Scalar<bool>("cpp.UseRunpath");

    /// <summary>Gets the explicit WasmModule setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppWasmModule> WasmModule { get; } = ArgumentKeys.Scalar<CppWasmModule>("cpp.WasmModule");

    /// <summary>Gets the explicit WasiEntry setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppWasiEntry> WasiEntry { get; } = ArgumentKeys.Scalar<CppWasiEntry>("cpp.WasiEntry");

    /// <summary>Gets the explicit WasmEnvironment setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> WasmEnvironment { get; } = ArgumentKeys.Scalar<string>("cpp.WasmEnvironment");

    /// <summary>Gets Emscripten initial memory in bytes; omission keeps the tool default.</summary>
    [GenerateArgument]
    public static ArgumentKey<long> WasmInitialMemory { get; } = ArgumentKeys.Scalar<long>("cpp.WasmInitialMemory");

    /// <summary>Gets Emscripten maximum memory in bytes; omission keeps the tool default.</summary>
    [GenerateArgument]
    public static ArgumentKey<long> WasmMaximumMemory { get; } = ArgumentKeys.Scalar<long>("cpp.WasmMaximumMemory");

    /// <summary>Gets Emscripten stack size in bytes; omission keeps the tool default.</summary>
    [GenerateArgument]
    public static ArgumentKey<long> WasmStackSize { get; } = ArgumentKeys.Scalar<long>("cpp.WasmStackSize");

    /// <summary>Gets the explicit WasmMemoryGrowth setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> WasmMemoryGrowth { get; } = ArgumentKeys.Scalar<bool>("cpp.WasmMemoryGrowth");

    /// <summary>Gets the explicit WasmModularize setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> WasmModularize { get; } = ArgumentKeys.Scalar<bool>("cpp.WasmModularize");

    /// <summary>Gets the explicit WasmExportName setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> WasmExportName { get; } = ArgumentKeys.Scalar<string>("cpp.WasmExportName");

    /// <summary>Gets the explicit LLVM Wasm legacy-EH backend mode; it requires Wasm exceptions and a compatible backend.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> WasmLegacyExceptions { get; } = ArgumentKeys.Scalar<bool>("cpp.WasmLegacyExceptions");

    /// <summary>Gets the explicit BigObject setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> BigObject { get; } = ArgumentKeys.Scalar<bool>("cpp.BigObject");

    /// <summary>Gets the explicit FullSourcePaths setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> FullSourcePaths { get; } = ArgumentKeys.Scalar<bool>("cpp.FullSourcePaths");

    /// <summary>Gets the explicit ConformingPreprocessor setting.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> ConformingPreprocessor { get; } = ArgumentKeys.Scalar<bool>("cpp.ConformingPreprocessor");

    /// <summary>Gets ordered Inputs; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<string>> Inputs { get; } = ArgumentKeys.Sequence<string>("cpp.Inputs", value => value);

    /// <summary>Gets ordered Includes; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<string>> Includes { get; } = ArgumentKeys.Sequence<string>("cpp.Includes", value => value);

    /// <summary>Gets ordered SystemIncludes; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<string>> SystemIncludes { get; } = ArgumentKeys.Sequence<string>("cpp.SystemIncludes", value => value);

    /// <summary>Gets ordered ForcedIncludes; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<string>> ForcedIncludes { get; } = ArgumentKeys.Sequence<string>("cpp.ForcedIncludes", value => value);

    /// <summary>Gets ordered Undefines; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<string>> Undefines { get; } = ArgumentKeys.Sequence<string>("cpp.Undefines", value => value);

    /// <summary>Gets ordered LibraryDirectories; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<string>> LibraryDirectories { get; } = ArgumentKeys.Sequence<string>("cpp.LibraryDirectories", value => value);

    /// <summary>Gets ordered FrameworkDirectories; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<string>> FrameworkDirectories { get; } = ArgumentKeys.Sequence<string>("cpp.FrameworkDirectories", value => value);

    /// <summary>Gets ordered Frameworks; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<string>> Frameworks { get; } = ArgumentKeys.Sequence<string>("cpp.Frameworks", value => value);

    /// <summary>Gets ordered Rpaths; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<string>> Rpaths { get; } = ArgumentKeys.Sequence<string>("cpp.Rpaths", value => value);

    /// <summary>Gets ordered Exports; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<string>> Exports { get; } = ArgumentKeys.Sequence<string>("cpp.Exports", value => value);

    /// <summary>Gets ordered NoDefaultLibraries; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<string>> NoDefaultLibraries { get; } = ArgumentKeys.Sequence<string>("cpp.NoDefaultLibraries", value => value);

    /// <summary>Gets ordered WasmExports; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<string>> WasmExports { get; } = ArgumentKeys.Sequence<string>("cpp.WasmExports", value => value);

    /// <summary>Gets ordered WasmRuntimeExports; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<string>> WasmRuntimeExports { get; } = ArgumentKeys.Sequence<string>("cpp.WasmRuntimeExports", value => value);

    /// <summary>Gets ordered Sanitizers; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<CppSanitizer>> Sanitizers { get; } = ArgumentKeys.Sequence<CppSanitizer>("cpp.Sanitizers", value => value);

    /// <summary>Gets ordered Raw; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<CppRawArgument>> Raw { get; } = ArgumentKeys.Sequence<CppRawArgument>("cpp.Raw", value => value);

    /// <summary>Gets ordered LinkInputs; intentional repetitions are retained.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<CppLinkInput>> LinkInputs { get; } = ArgumentKeys.Sequence<CppLinkInput>("cpp.LinkInputs", value => value);

    /// <summary>Gets named macro definitions; null is a valueless definition and empty string is an empty replacement.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyDictionary<string, string?>> Defines { get; } = ArgumentKeys.Map<string?>("cpp.Defines", value => value);

    /// <summary>Gets individual warning controls; true enables and false disables.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyDictionary<string, bool>> WarningControls { get; } = ArgumentKeys.Map<bool>("cpp.WarningControls", value => value);

    /// <summary>Gets explicit C++ runtime inputs, placed after user link inputs. Static libc++ requires its ABI library as well.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<CppLinkInput>> RuntimeLibraries { get; } = ArgumentKeys.Sequence<CppLinkInput>("cpp.RuntimeLibraries", value => value);

    /// <summary>Gets the compiler support runtime family; it is independent of StandardLibrary.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppCompilerRuntime> CompilerRuntime { get; } = ArgumentKeys.Scalar<CppCompilerRuntime>("cpp.CompilerRuntime");

    /// <summary>Gets an explicit linker implementation name or Clang-supported absolute linker path.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> LinkerSelection { get; } = ArgumentKeys.Scalar<string>("cpp.LinkerSelection");

    /// <summary>Gets a retained Apple LTO object path for subsequent debug-symbol processing.</summary>
    [GenerateArgument]
    public static ArgumentKey<string> LtoObjectPath { get; } = ArgumentKeys.Scalar<string>("cpp.LtoObjectPath");

    /// <summary>Gets explicit PCH creation or consumption inputs.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppPch> Pch { get; } = new("cpp.Pch", value => value, ArgumentKeys.RequireEqual);

    /// <summary>Gets dependency output configuration.</summary>
    [GenerateArgument]
    public static ArgumentKey<CppDependencies> Dependencies { get; } = new("cpp.Dependencies", value => value, ArgumentKeys.RequireEqual);
}
