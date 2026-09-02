namespace Incant.Core.Toolchains;

/// <summary>Identifies a supported compilation target platform.</summary>
public enum TargetPlatform
{
    /// <summary>The target platform is unknown.</summary>
    Unknown,

    /// <summary>Microsoft Windows.</summary>
    Windows,

    /// <summary>Linux.</summary>
    Linux,

    /// <summary>Apple macOS.</summary>
    MacOS,

    /// <summary>Apple iOS devices.</summary>
    IOS,

    /// <summary>The Apple iOS simulator.</summary>
    IOSSimulator,

    /// <summary>Apple tvOS devices.</summary>
    TvOS,

    /// <summary>The Apple tvOS simulator.</summary>
    TvOSSimulator,

    /// <summary>Apple watchOS devices.</summary>
    WatchOS,

    /// <summary>The Apple watchOS simulator.</summary>
    WatchOSSimulator,

    /// <summary>Apple visionOS devices.</summary>
    VisionOS,

    /// <summary>The Apple visionOS simulator.</summary>
    VisionOSSimulator,

    /// <summary>Android.</summary>
    Android,

    /// <summary>The Emscripten browser and JavaScript environment.</summary>
    Emscripten,

    /// <summary>The WebAssembly system interface.</summary>
    Wasi,
}
