using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SB.Core
{
    public static partial class Windows
    {
        [DllImport("kernel32", ExactSpelling = true)]
        public static extern uint GetLogicalDriveStringsW(uint nBufferLength, IntPtr lpBuffer);

        public static string[] EnumLogicalDrives()
        {
            var Disks = Environment.GetLogicalDrives();
            for (int i = 0; i < Disks.Length; i++)
                Disks[i] = Disks[i].Replace(":\\", "");
            return Disks;
        }
    }
}
