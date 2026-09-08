namespace Incant.CXLegacy.Arguments;

/// <summary>Optimization preference; it never selects floating-point relaxation or LTO implicitly.</summary>
public enum Optimization
{
    /// <summary>Disables this feature explicitly.</summary>
    None,
    /// <summary>Favors speed over conservative or size-oriented behavior.</summary>
    Fast,
    /// <summary>Enables the next speed optimization level where the frontend distinguishes it.</summary>
    Faster,
    /// <summary>Selects the highest general speed optimization level without implicit LTO.</summary>
    Fastest,
    /// <summary>Optimizes for code size without implicit LTO.</summary>
    Smallest,
}

/// <summary>Floating-point transformation policy, independent of optimization level.</summary>
public enum FloatingPoint
{
    /// <summary>Preserves normal floating-point semantics.</summary>
    Precise,
    /// <summary>Favors speed over conservative or size-oriented behavior.</summary>
    Fast,
    /// <summary>Requests strict floating-point evaluation and rounding behavior.</summary>
    Strict,
}

/// <summary>Diagnostic breadth mapped to the selected frontend; unsupported breadth is an error.</summary>
public enum WarningLevel
{
    /// <summary>Disables this feature explicitly.</summary>
    None,
    /// <summary>Preserves the tool default.</summary>
    Default,
    /// <summary>Enables the common warning set.</summary>
    All,
    /// <summary>Adds the frontend supplementary warnings.</summary>
    Extra,
    /// <summary>Requests all frontend diagnostics; GCC has no equivalent mode.</summary>
    Everything,
}

/// <summary>Debug-information storage. Linker PDB generation is distinct from compiler PDB generation.</summary>
public enum DebugFormat
{
    /// <summary>Disables this feature explicitly.</summary>
    None,
    /// <summary>Embeds debug information in object files.</summary>
    Embedded,
    /// <summary>Requests Microsoft PDB debug information.</summary>
    ProgramDatabase,
}

/// <summary>Link-time optimization mode shared by compilation, archive capability checks and linking.</summary>
public enum Lto
{
    /// <summary>Disables this feature explicitly.</summary>
    None,
    /// <summary>Enables full-program link-time optimization.</summary>
    Full,
    /// <summary>Enables LLVM ThinLTO with a confirmed supporting version.</summary>
    Thin,
}

/// <summary>Explicit exception implementation; Wasm and JavaScript exceptions are distinct modes.</summary>
public enum ExceptionMode
{
    /// <summary>Disables exception handling.</summary>
    Disabled,
    /// <summary>Uses the frontend native exception implementation.</summary>
    Native,
    /// <summary>Uses WebAssembly exception instructions.</summary>
    Wasm,
    /// <summary>Uses Emscripten JavaScript exception emulation.</summary>
    EmscriptenJavaScript,
}

/// <summary>C++ standard library selection; omission preserves the compiler default.</summary>
public enum StandardLibrary
{
    /// <summary>Preserves the tool default.</summary>
    Default,
    /// <summary>Selects the GNU C++ standard library.</summary>
    LibStdCpp,
    /// <summary>Selects the LLVM C++ standard library.</summary>
    LibCpp,
}

/// <summary>C++ runtime linkage. Explicit static libc++ outside bundles requires runtime archive inputs.</summary>
public enum RuntimeLinkage
{
    /// <summary>Preserves the tool default.</summary>
    Default,
    /// <summary>Links the C++ runtime statically.</summary>
    Static,
    /// <summary>Uses the supported shared C++ runtime.</summary>
    Shared,
}

/// <summary>Microsoft CRT selection, shared by compiler directives and direct linker defaults.</summary>
public enum WindowsRuntime
{
    /// <summary>Multithreaded DLL release CRT.</summary>
    MD,
    /// <summary>Multithreaded DLL debug CRT.</summary>
    MDd,
    /// <summary>Multithreaded static release CRT.</summary>
    MT,
    /// <summary>Multithreaded static debug CRT.</summary>
    MTd,
}

/// <summary>Compiler support library selection, independent of the C++ standard library.</summary>
public enum CompilerRuntime
{
    /// <summary>Retain the compiler default.</summary>
    Default,
    /// <summary>Use GCC's support runtime.</summary>
    LibGcc,
    /// <summary>Use LLVM compiler-rt.</summary>
    CompilerRt,
}

/// <summary>Default symbol visibility for supported native compiler dialects.</summary>
public enum Visibility
{
    /// <summary>Preserves the tool default.</summary>
    Default,
    /// <summary>Hides symbols from external dynamic lookup.</summary>
    Hidden,
    /// <summary>Uses ELF protected visibility where supported.</summary>
    Protected,
}

/// <summary>Requested instruction-set extension; it is never translated to an unrelated architecture.</summary>
public enum InstructionSet
{
    /// <summary>Enables the Sse instruction-set extension on a compatible architecture.</summary>
    Sse,
    /// <summary>Enables the Sse2 instruction-set extension on a compatible architecture.</summary>
    Sse2,
    /// <summary>Enables the Sse3 instruction-set extension on a compatible architecture.</summary>
    Sse3,
    /// <summary>Enables the Ssse3 instruction-set extension on a compatible architecture.</summary>
    Ssse3,
    /// <summary>Enables the Sse41 instruction-set extension on a compatible architecture.</summary>
    Sse41,
    /// <summary>Enables the Sse42 instruction-set extension on a compatible architecture.</summary>
    Sse42,
    /// <summary>Enables the Avx instruction-set extension on a compatible architecture.</summary>
    Avx,
    /// <summary>Enables the Avx2 instruction-set extension on a compatible architecture.</summary>
    Avx2,
    /// <summary>Enables the Avx512 instruction-set extension on a compatible architecture.</summary>
    Avx512,
    /// <summary>Enables the Neon instruction-set extension on a compatible architecture.</summary>
    Neon,
    /// <summary>Enables the WasmSimd128 instruction-set extension on a compatible architecture.</summary>
    WasmSimd128,
}

/// <summary>Runtime instrumentation; only combinations supported by the selected frontend are accepted.</summary>
public enum Sanitizer
{
    /// <summary>Detects invalid memory accesses.</summary>
    Address,
    /// <summary>Detects selected undefined behavior.</summary>
    Undefined,
    /// <summary>Detects data races.</summary>
    Thread,
    /// <summary>Detects uninitialized memory reads.</summary>
    Memory,
    /// <summary>Detects memory leaks.</summary>
    Leak,
    /// <summary>Detects invalid memory accesses using hardware tags.</summary>
    HardwareAddress,
}

/// <summary>Whether this compilation produces or consumes a precompiled header.</summary>
public enum PchMode
{
    /// <summary>Produces a new artifact; replacement cleanup is owned by the caller.</summary>
    Create,
    /// <summary>Consumes a previously created artifact.</summary>
    Use,
}

/// <summary>PCH inputs and outputs. The caller schedules creation and any required object consumption.</summary>
public sealed record Pch(PchMode Mode, string? Header, string Artifact, string? ObjectOutput = null);

/// <summary>Compiler dependency output format; it does not schedule dependent commands.</summary>
public enum DependencyMode
{
    /// <summary>Writes a GCC/Clang make-style dependency file.</summary>
    Depfile,
    /// <summary>Writes MSVC structured source dependencies.</summary>
    SourceDependencies,
    /// <summary>Writes include tracing to compiler output.</summary>
    ShowIncludes,
}

/// <summary>Dependency reporting independent of PCH or compilation scheduling.</summary>
public sealed record Dependencies(DependencyMode Mode, string? Path = null, string? ObjectName = null);

/// <summary>Emscripten dynamic module mode used consistently during compilation and linking.</summary>
public enum WasmModule
{
    /// <summary>Disables this feature explicitly.</summary>
    None,
    /// <summary>Creates an Emscripten main module with retained dynamic symbols.</summary>
    Main,
    /// <summary>Creates a main module with explicit dead-code elimination.</summary>
    MainDeadCodeElimination,
    /// <summary>Creates an Emscripten side module with retained symbols.</summary>
    Side,
    /// <summary>Creates a side module with explicit dead-code elimination.</summary>
    SideDeadCodeElimination,
}

/// <summary>WASI executable initialization model.</summary>
public enum WasiEntry
{
    /// <summary>Preserves the tool default.</summary>
    Default,
    /// <summary>Runs a WASI command entry point.</summary>
    Command,
    /// <summary>Creates a WASI reactor initialized by the host.</summary>
    Reactor,
}
