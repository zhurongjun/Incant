namespace Incant.AutoTest.CppToolchain.Setup;

internal sealed record SetupOptions(
    EnvironmentDefinition Profile,
    string Workspace,
    string EnvironmentPath,
    string ReportPath,
    string WorkRoot,
    string ToolchainRoot);

internal sealed record SetupParseResult(SetupOptions? Options, int ExitCode);

internal sealed class SetupConfigurationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
