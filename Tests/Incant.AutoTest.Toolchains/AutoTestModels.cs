using Incant.Core.Cpp;
using Incant.Core.Cpp.FindSdk;
using Incant.Core.Cpp.FindTools;

/// <summary>Identifies the operation selected through the command tree.</summary>
internal enum AutoTestOperation
{
    Discover,

    Verify,

    VerifyClangCl,
}

/// <summary>Identifies the Windows linker exercised with the clang-cl driver.</summary>
internal enum ClangClLinker
{
    Msvc,

    Lld,
}

/// <summary>Contains the immutable values captured from one parsed command.</summary>
internal sealed class AutoTestCommand
{
    internal required AutoTestOperation Operation { get; init; }

    internal AutoTestKind? Kind { get; init; }

    internal TargetPlatform? Target { get; init; }

    internal TargetArchitecture? Architecture { get; init; }

    internal int? ProductMajor { get; init; }

    internal int? CompilerMajor { get; init; }

    internal int? SdkMajor { get; init; }

    internal int? MsvcMajor { get; init; }

    internal int MinimumCount { get; init; } = 1;

    internal IReadOnlyCollection<ComponentKind> RequiredComponents { get; init; } = [];

    internal string? ExplicitRoot { get; init; }

    internal bool IncludePreview { get; init; }

    internal ClangClLinker? ClangClLinker { get; init; }

    internal string? JsonPath { get; init; }
}

/// <summary>Represents the result of parsing before any discovery work begins.</summary>
internal sealed record AutoTestParseResult(AutoTestCommand? Command, int ExitCode);

/// <summary>Preserves independently discovered tools and SDKs alongside one explicit smoke configuration.</summary>
internal sealed class AutoTestRun(string name, IReadOnlyList<ToolSet> toolSets, IEnumerable<Diagnostic> diagnostics)
{
    internal string Name { get; } = name;

    internal IReadOnlyList<ToolSet> ToolSets { get; } = toolSets;

    internal List<Sdk> Sdks { get; } = [];

    internal List<Diagnostic> Diagnostics { get; } = diagnostics.ToList();

    internal int DiscoveredInstallationCount { get; set; }

    internal SmokeConfiguration? Configuration { get; set; }

    internal IReadOnlyList<ToolchainSmokeResult> SmokeTests { get; set; } = [];
}

/// <summary>Contains only AutoTest's selected build inputs; Core does not construct or own this combination.</summary>
internal sealed record SmokeConfiguration(
    ToolSet ToolSet,
    Sdk? Sdk,
    TargetLayout Layout,
    string TargetTriple,
    ToolSet? MsvcToolSet,
    Sdk? MsvcSdk,
    Sdk? CompilerSdk)
{
    internal TargetPlatform TargetPlatform => Layout.Platform;

    internal TargetArchitecture TargetArchitecture => Layout.Architecture;

    internal required Tool CCompiler { get; init; }

    internal required Tool CppCompiler { get; init; }

    internal Tool? Linker { get; init; }

    internal TargetLayout? MsvcLayout { get; init; }

    internal TargetLayout? CompilerLayout { get; init; }
}

/// <summary>Preserves the existing command-line family names, including the SDK-only WindowsSdk case.</summary>
internal enum AutoTestKind
{
    VisualStudio,

    WindowsSdk,

    Gnu,

    Llvm,

    Xcode,

    AndroidNdk,

    Emscripten,

    WasiSdk,
}

/// <summary>Existing command-line role names, mapped to concrete tool names only by AutoTest.</summary>
internal enum ComponentKind
{
    Compiler,

    CppCompiler,

    Linker,

    Archiver,

    Ranlib,

    ResourceDirectory,

    Sysroot,
}

/// <summary>Records compilation and optional execution for one source language.</summary>
internal sealed record ToolchainSmokeResult(
    string Language,
    string CompilerPath,
    string? LinkerPath,
    string TargetTriple,
    bool CompilationSucceeded,
    string CompilationStandardOutput,
    string CompilationStandardError,
    bool Executed,
    bool? ExecutionSucceeded,
    string ExecutionStandardOutput,
    string ExecutionStandardError,
    string? ExecutionSkipReason,
    string? Error)
{
    internal bool IsSuccess => CompilationSucceeded && ExecutionSucceeded is not false;
}

/// <summary>Signals a failed real-host verification without treating it as a command-line error.</summary>
internal sealed class AutoTestFailureException : Exception
{
    internal AutoTestFailureException(string message)
        : base(message)
    {
    }
}
