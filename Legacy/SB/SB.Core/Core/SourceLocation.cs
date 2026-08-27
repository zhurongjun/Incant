using System.Runtime.CompilerServices;

namespace SB.Core
{
    public static class SourceLocation
    {
        public static string Directory([CallerFilePath] string? path = null) => Path.GetDirectoryName(path)!;
        public static string File([CallerFilePath] string? path = null) => path!;
        public static int Line([CallerLineNumber] int line = 0) => line;
        public static string MemberName([CallerMemberName] string? member = null) => member!;
        public static string BuildTempPath => Path.GetTempPath();
    }
}