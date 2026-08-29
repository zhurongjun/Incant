using Incant.Base;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;
using RuntimeInformation = System.Runtime.InteropServices.RuntimeInformation;

namespace Incant.UnitTests;

public sealed class PlatformTests
{
    [Fact]
    public void OSValuesUseStableOrdering()
    {
        Assert.Equal(0, (int)PlatformOS.Unknown);
        Assert.Equal(1, (int)PlatformOS.Windows);
        Assert.Equal(2, (int)PlatformOS.Linux);
        Assert.Equal(3, (int)PlatformOS.OSX);
    }

    [Fact]
    public void ArchValuesUseStableOrdering()
    {
        Assert.Equal(0, (int)PlatformArch.Unknown);
        Assert.Equal(1, (int)PlatformArch.X86);
        Assert.Equal(2, (int)PlatformArch.X64);
        Assert.Equal(3, (int)PlatformArch.ARM64);
    }

    [Fact]
    public void DetectedOSMatchesTheRuntime()
    {
        PlatformOS expectedOS = GetExpectedOS();

        Assert.Equal(expectedOS, Platform.OS);
    }

    [Fact]
    public void OSHelpersMatchTheDetectedValue()
    {
        Assert.Equal(
            Platform.OS == PlatformOS.Windows,
            Platform.OSIsWindows);
        Assert.Equal(
            Platform.OS == PlatformOS.Linux,
            Platform.OSIsLinux);
        Assert.Equal(
            Platform.OS == PlatformOS.OSX,
            Platform.OSIsOSX);
    }

    [Fact]
    public void DetectedArchMatchesTheRuntime()
    {
        PlatformArch expectedArch = RuntimeInformation.OSArchitecture switch
        {
            RuntimeArchitecture.X86 => PlatformArch.X86,
            RuntimeArchitecture.X64 => PlatformArch.X64,
            RuntimeArchitecture.Arm64 => PlatformArch.ARM64,
            _ => PlatformArch.Unknown,
        };

        Assert.Equal(expectedArch, Platform.Arch);
    }

    [Fact]
    public void ArchHelpersMatchTheDetectedValue()
    {
        Assert.Equal(
            Platform.Arch == PlatformArch.X86,
            Platform.ArchIsX86);
        Assert.Equal(
            Platform.Arch == PlatformArch.X64,
            Platform.ArchIsX64);
        Assert.Equal(
            Platform.Arch == PlatformArch.ARM64,
            Platform.ArchIsARM64);
    }

    private static PlatformOS GetExpectedOS()
    {
        if (System.OperatingSystem.IsWindows())
        {
            return PlatformOS.Windows;
        }
        else if (System.OperatingSystem.IsLinux())
        {
            return PlatformOS.Linux;
        }
        else if (System.OperatingSystem.IsMacOS())
        {
            return PlatformOS.OSX;
        }
        else
        {
            return PlatformOS.Unknown;
        }
    }
}
