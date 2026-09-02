namespace Incant.Core.Toolchains;

/// <summary>Identifies a toolchain executable or resource.</summary>
public enum ComponentKind
{
    /// <summary>A C compiler.</summary>
    Compiler,

    /// <summary>A C++ compiler.</summary>
    CppCompiler,

    /// <summary>A linker.</summary>
    Linker,

    /// <summary>A static-library archiver.</summary>
    Archiver,

    /// <summary>A static-library symbol-table generator.</summary>
    Ranlib,

    /// <summary>A compiler resource directory.</summary>
    ResourceDirectory,

    /// <summary>A target sysroot.</summary>
    Sysroot,
}
