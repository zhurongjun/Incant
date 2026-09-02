namespace Incant.Core.Toolchains;

/// <summary>Identifies a supported target processor architecture.</summary>
public enum TargetArchitecture
{
    /// <summary>The architecture is unknown.</summary>
    Unknown,

    /// <summary>The 32-bit x86 architecture.</summary>
    X86,

    /// <summary>The 64-bit x86 architecture.</summary>
    X64,

    /// <summary>The 32-bit Arm architecture.</summary>
    ARM,

    /// <summary>The 64-bit Arm architecture.</summary>
    ARM64,

    /// <summary>The 32-bit WebAssembly architecture.</summary>
    Wasm32,
}
