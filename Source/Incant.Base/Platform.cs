using System.Runtime.CompilerServices;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;
using RuntimeInformation = System.Runtime.InteropServices.RuntimeInformation;

namespace Incant.Base;

/// <summary>Identifies an operating system supported by platform detection.</summary>
public enum PlatformOS
{
    /// <summary>The operating system is not recognized.</summary>
    Unknown,

    /// <summary>Microsoft Windows.</summary>
    Windows,

    /// <summary>Linux.</summary>
    Linux,

    /// <summary>Apple macOS.</summary>
    OSX,
}

/// <summary>Identifies a processor architecture supported by platform detection.</summary>
public enum PlatformArch
{
    /// <summary>The processor architecture is not recognized.</summary>
    Unknown,

    /// <summary>The 32-bit x86 architecture.</summary>
    X86,

    /// <summary>The 64-bit x86 architecture.</summary>
    X64,

    /// <summary>The 64-bit Arm architecture.</summary>
    ARM64,
}

/// <summary>Exposes the operating system and operating-system architecture detected for the current host.</summary>
public static partial class Platform
{
    /// <summary>Gets the operating system detected when this type is initialized.</summary>
    public static readonly PlatformOS OS = DetectOS();

    /// <summary>Gets the operating-system architecture detected when this type is initialized.</summary>
    public static readonly PlatformArch Arch = DetectArch();

    /// <summary>Gets a value indicating whether the detected operating system is Windows.</summary>
    public static bool OSIsWindows
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => OS == PlatformOS.Windows;
    }

    /// <summary>Gets a value indicating whether the detected operating system is Linux.</summary>
    public static bool OSIsLinux
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => OS == PlatformOS.Linux;
    }

    /// <summary>Gets a value indicating whether the detected operating system is macOS.</summary>
    public static bool OSIsOSX
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => OS == PlatformOS.OSX;
    }

    /// <summary>Gets a value indicating whether the detected architecture is 32-bit x86.</summary>
    public static bool ArchIsX86
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Arch == PlatformArch.X86;
    }

    /// <summary>Gets a value indicating whether the detected architecture is 64-bit x86.</summary>
    public static bool ArchIsX64
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Arch == PlatformArch.X64;
    }

    /// <summary>Gets a value indicating whether the detected architecture is 64-bit Arm.</summary>
    public static bool ArchIsARM64
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Arch == PlatformArch.ARM64;
    }

    private static PlatformOS DetectOS()
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

    private static PlatformArch DetectArch()
    {
        switch (RuntimeInformation.OSArchitecture)
        {
            case RuntimeArchitecture.X86:
                return PlatformArch.X86;
            case RuntimeArchitecture.X64:
                return PlatformArch.X64;
            case RuntimeArchitecture.Arm64:
                return PlatformArch.ARM64;
            default:
                return PlatformArch.Unknown;
        }
    }
}
