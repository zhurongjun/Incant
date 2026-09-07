using Incant.Core.Cpp;
using Incant.Core.Cpp.FindSdk;

namespace Incant.AutoTest.CppToolchain;

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
