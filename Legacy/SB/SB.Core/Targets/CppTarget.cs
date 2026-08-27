using SB.Core;
using System.Runtime.CompilerServices;

namespace SB
{
    using BS = BuildInstance;
    public partial class CppTarget : Target
    {
        public CppTarget(BuildInstance Instance, string Name, bool IsFromPackage, [CallerFilePath] string? Location = null, [CallerLineNumber] int LineNumber = 0)
            : base(Instance, Name, IsFromPackage, Location, LineNumber)
        {

        }
    }
}
