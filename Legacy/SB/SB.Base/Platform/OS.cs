using System.Runtime.CompilerServices;
using DotnetRuntimeInfo = System.Runtime.InteropServices.RuntimeInformation;
namespace SB;

public enum PlatformOS
{
    Unknown,
    Windows,
    Linux,
    OSX
}
public enum PlatformArch
{
    Unknown,
    X86,
    X64,
    ARM64
}

public static partial class Platform
{
    public static readonly PlatformOS OS = _DetectOS();
    public static readonly PlatformArch Arch = _DetectArch();

    // os helpers
    public static bool OSIsWindows
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => OS == PlatformOS.Windows;
    }
    public static bool OSIsLinux
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => OS == PlatformOS.Linux;
    }
    public static bool OSIsOSX
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => OS == PlatformOS.OSX;
    }

    // arch helpers
    public static bool ArchIsX86
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Arch == PlatformArch.X86;
    }
    public static bool ArchIsX64
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Arch == PlatformArch.X64;
    }
    public static bool ArchIsARM64
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Arch == PlatformArch.ARM64;
    }

    private static PlatformOS _DetectOS()
    {
        if (OperatingSystem.IsWindows())
            return PlatformOS.Windows;
        else if (OperatingSystem.IsLinux())
            return PlatformOS.Linux;
        else if (OperatingSystem.IsMacOS())
            return PlatformOS.OSX;
        else
            return PlatformOS.Unknown;
    }
    private static PlatformArch _DetectArch()
    {
        switch (DotnetRuntimeInfo.OSArchitecture)
        {
            case System.Runtime.InteropServices.Architecture.X86:
                return PlatformArch.X86;
            case System.Runtime.InteropServices.Architecture.X64:
                return PlatformArch.X64;
            case System.Runtime.InteropServices.Architecture.Arm64:
                return PlatformArch.ARM64;
            default:
                return PlatformArch.Unknown;
        }
    }
}
