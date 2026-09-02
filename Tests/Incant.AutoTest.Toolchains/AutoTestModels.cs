using Incant.Core.Toolchains;

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

    internal Kind? Kind { get; init; }

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

/// <summary>Combines one discovery catalog with the smoke tests performed against it.</summary>
internal sealed class AutoTestRun
{
    internal AutoTestRun(string name, Catalog catalog)
    {
        Name = name;
        Catalog = catalog;
    }

    internal string Name { get; }

    internal Catalog Catalog { get; }

    internal IReadOnlyList<ToolchainSmokeResult> SmokeTests { get; set; } = [];
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
