using SB.Core;

namespace SB
{
    public partial class BuildInstance
    {
        public static Architecture HostArch => HostInformation.HostArch;
        public static OSPlatform HostOS => HostInformation.HostOS;

        public Architecture TargetArch
        {
            get => _targetArch;
            set => _targetArch = value;
        }

        public OSPlatform TargetOS
        {
            get => _targetOS;
            set => _targetOS = value;
        }
    }
}
