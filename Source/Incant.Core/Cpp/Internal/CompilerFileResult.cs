namespace Incant.Core.Cpp;

internal sealed record CompilerFileResult(string? Path, IReadOnlyList<Diagnostic> Diagnostics);
