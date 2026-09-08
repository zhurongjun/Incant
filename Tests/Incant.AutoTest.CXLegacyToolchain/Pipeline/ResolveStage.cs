using Incant.Base;

namespace Incant.AutoTest.CXLegacyToolchain;

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
        foreach (ToolchainCandidate candidate in context.Candidates)
        {
            if (candidate.Status == CandidateStatus.Resolved && candidate.Toolchain is ResolvedToolchain toolchain)
            {
                foreach (string reason in BuildInputs.Missing(toolchain))
                {
                    candidate.Invalidate(reason);
                }
            }

            if (candidate.Status == CandidateStatus.Invalid && !candidate.Required)
            {
                candidate.Skip(string.Join(" ", candidate.Failures));
                candidate.Failures.Clear();
            }
        }

        return context.CandidatesSatisfy(
            candidate => candidate.Status == CandidateStatus.Resolved);
    }
}
