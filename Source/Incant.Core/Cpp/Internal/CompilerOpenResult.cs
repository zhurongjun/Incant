namespace Incant.Core.Cpp;

internal sealed record CompilerOpenResult(
    CompilerProbe? Probe,
    ProbeOutcome? Failure = null,
    string? Error = null);
