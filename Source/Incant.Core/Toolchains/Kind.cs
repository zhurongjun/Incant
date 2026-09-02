namespace Incant.Core.Toolchains;

/// <summary>Identifies a discoverable toolchain or SDK family.</summary>
public enum Kind
{
    /// <summary>Microsoft Visual Studio and MSVC.</summary>
    VisualStudio,

    /// <summary>A Windows platform SDK.</summary>
    WindowsSdk,

    /// <summary>A GNU compiler toolchain.</summary>
    Gnu,

    /// <summary>An LLVM compiler toolchain.</summary>
    Llvm,

    /// <summary>An Apple Xcode toolchain.</summary>
    Xcode,

    /// <summary>An Android native development kit.</summary>
    AndroidNdk,

    /// <summary>An Emscripten SDK.</summary>
    Emscripten,

    /// <summary>A WASI SDK.</summary>
    WasiSdk,
}
