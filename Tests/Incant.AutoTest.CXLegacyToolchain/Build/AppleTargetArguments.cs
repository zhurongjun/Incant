using Incant.CXLegacy;

namespace Incant.AutoTest.CXLegacyToolchain;

internal static class AppleTargetArguments
{
    internal static string Triple(TargetPlatform platform, TargetArchitecture architecture, Version? deploymentVersion)
    {
        string cpu = architecture switch
        {
            TargetArchitecture.ARM64 => "arm64",
            TargetArchitecture.X64 => "x86_64",
            _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, null),
        };
        string suffix = platform switch
        {
            TargetPlatform.MacOS => "macosx" + deploymentVersion,
            TargetPlatform.IOS => "ios" + deploymentVersion,
            TargetPlatform.IOSSimulator => "ios" + deploymentVersion + "-simulator",
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
        };
        return $"{cpu}-apple-{suffix}";
    }
}
