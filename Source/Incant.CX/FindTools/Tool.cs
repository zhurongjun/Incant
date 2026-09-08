using Incant.CX;

namespace Incant.CX.FindTools;

/// <summary>An existing executable resolved within one toolset.</summary>
/// <param name="Name">The requested tool role name.</param>
/// <param name="Path">The absolute invocation path, preserving an executable or vendor wrapper entry.</param>
/// <param name="HostArchitecture">The selected universal slice or launcher interpreter architecture, or Unknown when not established.</param>
/// <param name="TargetArchitecture">The selected target architecture, or Unknown for a target-independent tool.</param>
public sealed record Tool(
    string Name,
    string Path,
    TargetArchitecture HostArchitecture = TargetArchitecture.Unknown,
    TargetArchitecture TargetArchitecture = TargetArchitecture.Unknown);

/// <summary>Restricts a tool lookup to a host/target variant without choosing build flags.</summary>
public sealed record ToolQuery
{
    /// <summary>Gets the required host binary architecture; null prefers the current process architecture from a universal image or launcher.</summary>
    public TargetArchitecture? HostArchitecture { get; init; }

    /// <summary>Gets the required target binary variant, when the installation separates them.</summary>
    public TargetArchitecture? TargetArchitecture { get; init; }

    /// <summary>Gets an optional target platform, for environment-specific lookup such as xcrun.</summary>
    public TargetPlatform? TargetPlatform { get; init; }
}

/// <summary>Common concrete tool names, without a host executable suffix. Custom names are also accepted.</summary>
public static class ToolNames
{
    /// <summary>The MSVC compiler.</summary>
    public const string Cl = "cl";

    /// <summary>The MSVC linker.</summary>
    public const string Link = "link";

    /// <summary>The MSVC library manager.</summary>
    public const string Lib = "lib";

    /// <summary>The GNU C compiler.</summary>
    public const string Gcc = "gcc";

    /// <summary>The GNU C++ compiler.</summary>
    public const string Gxx = "g++";

    /// <summary>The GNU archive plugin wrapper.</summary>
    public const string GccAr = "gcc-ar";

    /// <summary>The native archiver.</summary>
    public const string Ar = "ar";

    /// <summary>The native linker.</summary>
    public const string Ld = "ld";

    /// <summary>The native archive indexer.</summary>
    public const string Ranlib = "ranlib";

    /// <summary>The Clang C driver.</summary>
    public const string Clang = "clang";

    /// <summary>The Clang C++ driver.</summary>
    public const string Clangxx = "clang++";

    /// <summary>The Clang MSVC-compatible driver.</summary>
    public const string ClangCl = "clang-cl";

    /// <summary>The LLVM archiver.</summary>
    public const string LlvmAr = "llvm-ar";

    /// <summary>The LLVM archive indexer.</summary>
    public const string LlvmRanlib = "llvm-ranlib";

    /// <summary>The LLVM library manager.</summary>
    public const string LlvmLib = "llvm-lib";

    /// <summary>The ELF LLVM linker.</summary>
    public const string LdLld = "ld.lld";

    /// <summary>The Windows LLVM linker.</summary>
    public const string LldLink = "lld-link";

    /// <summary>The WebAssembly LLVM linker.</summary>
    public const string WasmLd = "wasm-ld";

    /// <summary>The Emscripten C driver.</summary>
    public const string Emcc = "emcc";

    /// <summary>The Emscripten C++ driver.</summary>
    public const string Emxx = "em++";

    /// <summary>The Emscripten archiver.</summary>
    public const string Emar = "emar";

    /// <summary>The Emscripten archive indexer.</summary>
    public const string Emranlib = "emranlib";
}
