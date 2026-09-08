namespace Incant.CX.Arguments;

/// <summary>Fixed C-family configuration. Null properties are absent; empty collections are explicitly set.</summary>
public sealed partial class ArgumentSet
{
    /// <summary>Gets the source language; omission selects C++. It does not choose an executable.</summary>
    [Argument]
    public partial Language? Language { get; }

    /// <summary>Gets a language-matched standard such as c11, c++17 or gnu++20. Unsupported spellings are diagnosed.</summary>
    [Argument]
    public partial string? Standard { get; }

    /// <summary>Gets the output platform used for parameter applicability, without discovering SDK resources.</summary>
    [Argument]
    public partial TargetPlatform? Platform { get; }

    /// <summary>Gets the output architecture used for instruction-set checks and platform command options.</summary>
    [Argument]
    public partial TargetArchitecture? Architecture { get; }

    /// <summary>Gets a confirmed frontend version for version-sensitive features; ordinary operations allow omission.</summary>
    [Argument]
    public partial Version? CompilerVersion { get; }

    /// <summary>Gets an explicit driver target override. Omission preserves the invocation default.</summary>
    [Argument]
    public partial string? Triple { get; }

    /// <summary>Gets an explicit sysroot override, including a deliberate root slash. Paths are not discovered or canonicalized here.</summary>
    [Argument]
    public partial string? Sysroot { get; }

    /// <summary>Gets a selected layout: GNU 32/64/x32, Emscripten pic, WASI eh, or default dot. It is not an arbitrary compiler flag.</summary>
    [Argument]
    public partial string? Multilib { get; }

    /// <summary>Gets the Android API appended to an unversioned Android triple.</summary>
    [Argument]
    public partial int? AndroidApi { get; }

    /// <summary>Gets an Apple deployment version. Device and simulator platforms retain distinct target arguments.</summary>
    [Argument]
    public partial string? DeploymentVersion { get; }

    /// <summary>Gets the one command output path. Archive inspection uses this field to identify the existing archive.</summary>
    [Argument]
    public partial string? Output { get; }

    /// <summary>Gets the explicit OutputKind setting.</summary>
    [Argument]
    public partial OutputKind? OutputKind { get; }

    /// <summary>Gets the explicit Optimization setting.</summary>
    [Argument]
    public partial Optimization? Optimization { get; }

    /// <summary>Gets the explicit FloatingPoint setting.</summary>
    [Argument]
    public partial FloatingPoint? FloatingPoint { get; }

    /// <summary>Gets the explicit Exceptions setting.</summary>
    [Argument]
    public partial ExceptionMode? Exceptions { get; }

    /// <summary>Gets the explicit Rtti setting.</summary>
    [Argument]
    public partial bool? Rtti { get; }

    /// <summary>Gets the explicit PositionIndependent setting.</summary>
    [Argument]
    public partial bool? PositionIndependent { get; }

    /// <summary>Gets the explicit PositionIndependentExecutable setting.</summary>
    [Argument]
    public partial bool? PositionIndependentExecutable { get; }

    /// <summary>Gets the explicit Threads setting.</summary>
    [Argument]
    public partial bool? Threads { get; }

    /// <summary>Gets the explicit FunctionSections setting.</summary>
    [Argument]
    public partial bool? FunctionSections { get; }

    /// <summary>Gets the explicit DataSections setting.</summary>
    [Argument]
    public partial bool? DataSections { get; }

    /// <summary>Gets the explicit Visibility setting.</summary>
    [Argument]
    public partial Visibility? Visibility { get; }

    /// <summary>Gets the explicit Cpu setting.</summary>
    [Argument]
    public partial string? Cpu { get; }

    /// <summary>Gets the explicit InstructionSet setting.</summary>
    [Argument]
    public partial InstructionSet? InstructionSet { get; }

    /// <summary>Gets the explicit Warnings setting.</summary>
    [Argument]
    public partial WarningLevel? Warnings { get; }

    /// <summary>Gets the explicit WarningsAsErrors setting.</summary>
    [Argument]
    public partial bool? WarningsAsErrors { get; }

    /// <summary>Gets the explicit Debug setting.</summary>
    [Argument]
    public partial DebugFormat? Debug { get; }

    /// <summary>Gets a compiler or linker PDB path, interpreted independently for each operation.</summary>
    [Argument]
    public partial string? Pdb { get; }

    /// <summary>Gets explicit MSVC Dynamic Debug selection for compile, archive and link; version and incompatibilities are checked.</summary>
    [Argument]
    public partial bool? DynamicDebug { get; }

    /// <summary>Gets the explicit Lto setting.</summary>
    [Argument]
    public partial Lto? Lto { get; }

    /// <summary>Gets the explicit StandardLibrary setting.</summary>
    [Argument]
    public partial StandardLibrary? StandardLibrary { get; }

    /// <summary>Gets the explicit RuntimeLinkage setting.</summary>
    [Argument]
    public partial RuntimeLinkage? RuntimeLinkage { get; }

    /// <summary>Gets the explicit WindowsRuntime setting.</summary>
    [Argument]
    public partial WindowsRuntime? WindowsRuntime { get; }

    /// <summary>Gets the explicit ArchiveMode setting.</summary>
    [Argument]
    public partial ArchiveMode? ArchiveMode { get; }

    /// <summary>Gets the explicit ArchiveDialect setting.</summary>
    [Argument]
    public partial ArchiveDialect? ArchiveDialect { get; }

    /// <summary>Gets the caller-confirmed LTO capability of the selected archive/index tool; the driver never discovers a tool.</summary>
    [Argument]
    public partial bool? ArchiveSupportsLto { get; }

    /// <summary>Gets the explicit DeterministicArchive setting.</summary>
    [Argument]
    public partial bool? DeterministicArchive { get; }

    /// <summary>Gets the explicit LinkerDialect setting.</summary>
    [Argument]
    public partial LinkerDialect? LinkerDialect { get; }

    /// <summary>Gets the explicit EntryPoint setting.</summary>
    [Argument]
    public partial string? EntryPoint { get; }

    /// <summary>Gets the explicit ImportLibrary setting.</summary>
    [Argument]
    public partial string? ImportLibrary { get; }

    /// <summary>Gets the explicit Subsystem setting.</summary>
    [Argument]
    public partial string? Subsystem { get; }

    /// <summary>Gets ordered Windows manifest input files to embed while linking.</summary>
    [Argument]
    public partial IReadOnlyList<string>? ManifestInputs { get; }

    /// <summary>Gets the explicit DisableDefaultLibraries setting.</summary>
    [Argument]
    public partial bool? DisableDefaultLibraries { get; }

    /// <summary>Gets the explicit Soname setting.</summary>
    [Argument]
    public partial string? Soname { get; }

    /// <summary>Gets the explicit InstallName setting.</summary>
    [Argument]
    public partial string? InstallName { get; }

    /// <summary>Gets the explicit BundleLoader setting.</summary>
    [Argument]
    public partial string? BundleLoader { get; }

    /// <summary>Gets the explicit UseRunpath setting.</summary>
    [Argument]
    public partial bool? UseRunpath { get; }

    /// <summary>Gets the explicit WasmModule setting.</summary>
    [Argument]
    public partial WasmModule? WasmModule { get; }

    /// <summary>Gets the explicit WasiEntry setting.</summary>
    [Argument]
    public partial WasiEntry? WasiEntry { get; }

    /// <summary>Gets the explicit WasmEnvironment setting.</summary>
    [Argument]
    public partial string? WasmEnvironment { get; }

    /// <summary>Gets Emscripten initial memory in bytes; omission keeps the tool default.</summary>
    [Argument]
    public partial long? WasmInitialMemory { get; }

    /// <summary>Gets Emscripten maximum memory in bytes; omission keeps the tool default.</summary>
    [Argument]
    public partial long? WasmMaximumMemory { get; }

    /// <summary>Gets Emscripten stack size in bytes; omission keeps the tool default.</summary>
    [Argument]
    public partial long? WasmStackSize { get; }

    /// <summary>Gets the explicit WasmMemoryGrowth setting.</summary>
    [Argument]
    public partial bool? WasmMemoryGrowth { get; }

    /// <summary>Gets the explicit WasmModularize setting.</summary>
    [Argument]
    public partial bool? WasmModularize { get; }

    /// <summary>Gets the explicit WasmExportName setting.</summary>
    [Argument]
    public partial string? WasmExportName { get; }

    /// <summary>Gets the explicit LLVM Wasm legacy-EH backend mode; it requires Wasm exceptions and a compatible backend.</summary>
    [Argument]
    public partial bool? WasmLegacyExceptions { get; }

    /// <summary>Gets the explicit BigObject setting.</summary>
    [Argument]
    public partial bool? BigObject { get; }

    /// <summary>Gets the explicit FullSourcePaths setting.</summary>
    [Argument]
    public partial bool? FullSourcePaths { get; }

    /// <summary>Gets the explicit ConformingPreprocessor setting.</summary>
    [Argument]
    public partial bool? ConformingPreprocessor { get; }

    /// <summary>Gets ordered Inputs; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<string>? Inputs { get; }

    /// <summary>Gets ordered Includes; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<string>? Includes { get; }

    /// <summary>Gets ordered SystemIncludes; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<string>? SystemIncludes { get; }

    /// <summary>Gets ordered ForcedIncludes; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<string>? ForcedIncludes { get; }

    /// <summary>Gets ordered Undefines; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<string>? Undefines { get; }

    /// <summary>Gets ordered LibraryDirectories; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<string>? LibraryDirectories { get; }

    /// <summary>Gets ordered FrameworkDirectories; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<string>? FrameworkDirectories { get; }

    /// <summary>Gets ordered Frameworks; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<string>? Frameworks { get; }

    /// <summary>Gets ordered Rpaths; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<string>? Rpaths { get; }

    /// <summary>Gets ordered Exports; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<string>? Exports { get; }

    /// <summary>Gets ordered NoDefaultLibraries; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<string>? NoDefaultLibraries { get; }

    /// <summary>Gets ordered WasmExports; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<string>? WasmExports { get; }

    /// <summary>Gets ordered WasmRuntimeExports; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<string>? WasmRuntimeExports { get; }

    /// <summary>Gets ordered Sanitizers; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<Sanitizer>? Sanitizers { get; }

    /// <summary>Gets ordered Raw; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<RawArgument>? Raw { get; }

    /// <summary>Gets ordered LinkInputs; intentional repetitions are retained.</summary>
    [Argument]
    public partial IReadOnlyList<LinkInput>? LinkInputs { get; }

    /// <summary>Gets named macro definitions; null is a valueless definition and empty string is an empty replacement.</summary>
    [Argument]
    public partial IReadOnlyDictionary<string, string?>? Defines { get; }

    /// <summary>Gets individual warning controls; true enables and false disables.</summary>
    [Argument]
    public partial IReadOnlyDictionary<string, bool>? WarningControls { get; }

    /// <summary>Gets explicit C++ runtime inputs, placed after user link inputs. Static libc++ requires its ABI library as well.</summary>
    [Argument]
    public partial IReadOnlyList<LinkInput>? RuntimeLibraries { get; }

    /// <summary>Gets the compiler support runtime family; it is independent of StandardLibrary.</summary>
    [Argument]
    public partial CompilerRuntime? CompilerRuntime { get; }

    /// <summary>Gets an explicit linker implementation name or Clang-supported absolute linker path.</summary>
    [Argument]
    public partial string? LinkerSelection { get; }

    /// <summary>Gets a retained Apple LTO object path for subsequent debug-symbol processing.</summary>
    [Argument]
    public partial string? LtoObjectPath { get; }

    /// <summary>Gets explicit PCH creation or consumption inputs.</summary>
    [Argument]
    public partial Pch? Pch { get; }

    /// <summary>Gets dependency output configuration.</summary>
    [Argument]
    public partial Dependencies? Dependencies { get; }

}
