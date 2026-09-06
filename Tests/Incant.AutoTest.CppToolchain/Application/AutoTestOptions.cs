namespace Incant.AutoTest.CppToolchain;

internal sealed record AutoTestOptions(
    EnvironmentProfile Profile,
    string? EnvironmentPath,
    string ReportPath,
    string WorkRoot,
    bool KeepWork);

internal sealed record AutoTestParseResult(AutoTestOptions? Options, int ExitCode);
