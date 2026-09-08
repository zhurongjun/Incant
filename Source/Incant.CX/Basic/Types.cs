namespace Incant.CX;

/// <summary>Identifies a compiler entry rather than its SDK or installation family.</summary>
public enum CompilerKind
{
    /// <summary>The compiler kind is unknown.</summary>
    Unknown = 0,

    /// <summary>The Microsoft C/C++ compiler.</summary>
    Msvc,

    /// <summary>The GNU Compiler Collection driver.</summary>
    Gcc,

    /// <summary>The Clang GNU-style driver, including Android and WASI installations.</summary>
    Clang,

    /// <summary>The Clang driver with the Microsoft-compatible command-line interface.</summary>
    ClangCl,

    /// <summary>The Apple Clang driver.</summary>
    AppleClang,

    /// <summary>The Emscripten compiler driver.</summary>
    Emscripten,
}

/// <summary>Identifies an archive tool invocation entry.</summary>
public enum ArchiverKind
{
    /// <summary>The archive tool kind is unknown.</summary>
    Unknown = 0,

    /// <summary>The Microsoft librarian.</summary>
    MsvcLib,

    /// <summary>The GNU ar tool.</summary>
    GnuAr,

    /// <summary>The GCC ar wrapper with GCC plugin integration.</summary>
    GccAr,

    /// <summary>The LLVM ar tool.</summary>
    LlvmAr,

    /// <summary>The LLVM Microsoft-compatible librarian.</summary>
    LlvmLib,

    /// <summary>A BSD ar tool.</summary>
    BsdAr,

    /// <summary>The Apple libtool archive entry.</summary>
    AppleLibtool,

    /// <summary>The Emscripten ar wrapper.</summary>
    Emar,
}

/// <summary>Identifies the actual standalone linker or compiler driver invocation entry.</summary>
public enum LinkerKind
{
    /// <summary>The linker entry kind is unknown.</summary>
    Unknown = 0,

    /// <summary>The Microsoft linker.</summary>
    MsvcLink,

    /// <summary>The GNU BFD linker.</summary>
    GnuBfd,

    /// <summary>The GNU gold linker.</summary>
    GnuGold,

    /// <summary>The LLVM LLD ELF linker.</summary>
    LldElf,

    /// <summary>The LLVM Microsoft-compatible LLD linker.</summary>
    LldLink,

    /// <summary>The Apple ld linker.</summary>
    AppleLd,

    /// <summary>The LLVM LLD Mach-O linker.</summary>
    LldMachO,

    /// <summary>The LLVM WebAssembly linker.</summary>
    WasmLd,

    /// <summary>A GCC driver performing the link operation.</summary>
    GccDriver,

    /// <summary>A GNU-style Clang driver performing the link operation.</summary>
    ClangDriver,

    /// <summary>A Microsoft-compatible Clang driver performing the link operation.</summary>
    ClangClDriver,

    /// <summary>An Apple Clang driver performing the link operation.</summary>
    AppleClangDriver,

    /// <summary>An Emscripten driver performing the link operation.</summary>
    EmscriptenDriver,
}
