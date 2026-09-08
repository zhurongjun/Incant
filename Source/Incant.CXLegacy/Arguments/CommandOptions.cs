namespace Incant.CXLegacy.Arguments;

/// <summary>Compiler command syntax. Select the actual invocation family explicitly.</summary>
public enum Dialect
{
    /// <summary>Microsoft command syntax.</summary>
    Msvc,
    /// <summary>Clang with Microsoft-compatible command syntax.</summary>
    ClangCl,
    /// <summary>GNU command syntax.</summary>
    Gnu,
    /// <summary>Clang with GNU-style command syntax.</summary>
    Clang,
    /// <summary>AppleClang with GNU-style command syntax.</summary>
    AppleClang,
    /// <summary>Android NDK Clang command syntax.</summary>
    AndroidClang,
    /// <summary>Emscripten command syntax.</summary>
    Emscripten,
    /// <summary>WASI SDK Clang command syntax.</summary>
    WasiClang,
}

/// <summary>One command operation; tool selection and scheduling belong to the caller.</summary>
public enum Operation
{
    /// <summary>Compile behavior.</summary>
    Compile,
    /// <summary>Archive behavior.</summary>
    Archive,
    /// <summary>Link behavior.</summary>
    Link,
    /// <summary>Resource behavior.</summary>
    Resource,
}

/// <summary>Source language. When the language field is absent, compilation uses C++.</summary>
public enum Language
{
    /// <summary>C source language.</summary>
    C,
    /// <summary>C++ source language.</summary>
    Cpp,
    /// <summary>ObjectiveC source language.</summary>
    ObjectiveC,
    /// <summary>Objective-C++ source language.</summary>
    ObjectiveCpp,
}

/// <summary>Placement of an unsplit extension argument relative to generated options and inputs.</summary>
public enum RawPosition
{
    /// <summary>Appears before generated options.</summary>
    BeforeOptions,
    /// <summary>Appears before input operands.</summary>
    BeforeInputs,
    /// <summary>Appears after generated inputs and output options.</summary>
    AfterInputs,
}

/// <summary>Destination of an extension token; forwarding preserves the token boundary.</summary>
public enum ArgumentRoute
{
    /// <summary>Passes arguments to the compiler driver.</summary>
    Driver,
    /// <summary>Forwards a single token to the Clang frontend.</summary>
    Frontend,
    /// <summary>Forwards a single token to the linker.</summary>
    Linker,
}

/// <summary>One unsplit extension argument. Null selectors match all corresponding operations.</summary>
public sealed record RawArgument(
    string Value,
    RawPosition Position = RawPosition.BeforeInputs,
    ArgumentRoute Route = ArgumentRoute.Driver,
    Operation? Operation = null,
    Dialect? Dialect = null,
    Language? Language = null);
