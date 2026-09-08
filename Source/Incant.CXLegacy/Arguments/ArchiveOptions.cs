namespace Incant.CXLegacy.Arguments;

/// <summary>Archive operation. Creation requires the caller to remove any previous archive first.</summary>
public enum ArchiveMode
{
    /// <summary>Produces a new artifact; replacement cleanup is owned by the caller.</summary>
    Create,
    /// <summary>Appends new archive members without removing existing members.</summary>
    Append,
    /// <summary>Lists existing archive members without requiring new inputs.</summary>
    List,
    /// <summary>Refreshes an existing archive symbol index.</summary>
    Index,
}

/// <summary>Archive or indexer command syntax, independent of the compiler family.</summary>
public enum ArchiveDialect
{
    /// <summary>GNU command syntax.</summary>
    Gnu,
    /// <summary>LLVM archive command syntax.</summary>
    Llvm,
    /// <summary>Emscripten command syntax.</summary>
    Emscripten,
    /// <summary>Microsoft command syntax.</summary>
    Msvc,
    /// <summary>A standalone GNU-compatible ranlib indexer.</summary>
    Ranlib,
    /// <summary>Apple libtool static archive command syntax.</summary>
    AppleLibtool,
}
