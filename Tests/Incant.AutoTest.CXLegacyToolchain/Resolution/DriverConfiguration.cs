using Incant.CXLegacy;
using Incant.CXLegacy.FindSdk;

namespace Incant.AutoTest.CXLegacyToolchain;

internal sealed record DriverConfiguration(
    string? TargetTriple,
    string? SysrootPath,
    string? Multilib)
{
    internal static DriverConfiguration Native(
        TargetPlatform platform, TargetLayout compiler, TargetLayout? sdk = null) =>
        platform == TargetPlatform.Linux
            ? new DriverConfiguration(null, null, compiler.Multilib)
            : new DriverConfiguration(compiler.TargetTriple, sdk?.SysrootPath ?? compiler.SysrootPath,
                compiler.Multilib);
}
