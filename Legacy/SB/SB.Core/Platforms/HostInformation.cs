namespace SB.Core
{
    using DotNetArch = System.Runtime.InteropServices.Architecture;
    using DotnetRuntimeInfo = System.Runtime.InteropServices.RuntimeInformation;
    using DotnetOSPlatform = System.Runtime.InteropServices.OSPlatform;

    public class HostInformation
    {
        public static Architecture HostArch => archMap.TryGetValue(DotnetRuntimeInfo.OSArchitecture, out var arch) ? arch : throw new Exception($"Unsupported Platform Architecture: {DotnetRuntimeInfo.OSArchitecture}");
        
        public static OSPlatform HostOS =>
            DotnetRuntimeInfo.IsOSPlatform(DotnetOSPlatform.Windows) ? OSPlatform.Windows :
            DotnetRuntimeInfo.IsOSPlatform(DotnetOSPlatform.OSX) ? OSPlatform.OSX :
            DotnetRuntimeInfo.IsOSPlatform(DotnetOSPlatform.Linux) ? OSPlatform.Linux : throw new Exception($"Unsupported Host Platform OS!");

        private static readonly Dictionary<DotNetArch, Architecture> archMap = new Dictionary<DotNetArch, Architecture>
        {
            { DotNetArch.X86, Architecture.X86 },
            { DotNetArch.X64, Architecture.X64 },
            { DotNetArch.Arm64, Architecture.ARM64 }
        };
    }
}