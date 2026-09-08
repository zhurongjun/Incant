namespace Incant.AutoTest.CXLegacyToolchain;

internal enum PipelineStageStatus
{
    Pending,
    Passed,
    Failed,
    Skipped,
    Canceled,
}

internal sealed class PipelineStageResult
{
    internal required string Name { get; init; }

    internal PipelineStageStatus Status { get; set; } = PipelineStageStatus.Pending;

    internal TimeSpan Elapsed { get; set; }

    internal List<string> Messages { get; } = [];
}

internal sealed record PipelineStage(
    PipelineStageKind Kind,
    Func<AutoTestContext, CancellationToken, Task<bool>> ExecuteAsync)
{
    internal string Name => Kind.ToString();
}

internal sealed class AutoTestConfigurationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
