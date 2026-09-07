namespace Incant.TestSupport;

internal sealed record CompilerInvocation(
    Guid Id,
    int ProcessId,
    IReadOnlyList<string> Arguments,
    long StartedTimestamp,
    long? CompletedTimestamp = null,
    int? ExitCode = null);
