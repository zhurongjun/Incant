using Incant.Base;

namespace Incant.AutoTest.CppToolchain;

internal static class ResolveStage
{
    internal static async Task<bool> ExecuteAsync(
        AutoTestContext context,
        CancellationToken cancellationToken)
    {
        if (context.Profile.HostOS == PlatformOS.Windows)
        {
            await WindowsToolchainResolver.ResolveAsync(
                context, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await NativeToolchainResolver.ResolveAsync(
                context, cancellationToken).ConfigureAwait(false);
        }

        if (context.Profile.HostOS == PlatformOS.OSX)
        {
            await XcodeToolchainResolver.ResolveAsync(
                context, cancellationToken).ConfigureAwait(false);
        }

        await BundleToolchainResolver.ResolveAsync(
            context, cancellationToken).ConfigureAwait(false);
        ToolchainResolution.EnsureUniqueCandidateIds(context);
        ToolchainResolution.EnsureCoverage(context);
        return context.RequiredCandidatesSatisfy(
            candidate => candidate.Status == CandidateStatus.Resolved);
    }
}
