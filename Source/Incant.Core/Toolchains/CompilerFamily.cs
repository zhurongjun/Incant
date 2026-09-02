namespace Incant.Core.Toolchains;

/// <summary>Identifies the compiler implementation used by a toolchain.</summary>
public enum CompilerFamily
{
    /// <summary>The Microsoft C/C++ compiler.</summary>
    Msvc,

    /// <summary>The GNU compiler collection.</summary>
    Gcc,

    /// <summary>The LLVM Clang compiler.</summary>
    Clang,
}
