using System.Runtime.Versioning;
using System.Diagnostics;
using System.Collections;
using Serilog;
using System.Runtime.InteropServices;
using System.Linq;

namespace SB.Core
{
    public enum WindowsSDKStrategy
    {
        Default,
        FindLatest,
        UserSpecified
    };
    public partial class VisualStudio : IToolchain
    {
        public static void RegisterSetups(BuildInstance instance)
        {
            instance.AddSetup<VisualStudioSetup>();
        }

        // https://blog.pcitron.fr/2022/01/04/dont-use-vcvarsall-vsdevcmd/
        public bool FastFind => VSVersion >= 2022;

        public VisualStudio(
            int VSVersion,
            bool useClangCl = true,
            WindowsSDKStrategy windowsSDKStrategy = WindowsSDKStrategy.Default,
            Architecture? HostArch = null,
            Architecture? TargetArch = null)
        {
            this.VSVersion = VSVersion;
            UseClangCl = useClangCl;
            WindowsSDKStrategy = windowsSDKStrategy;
            this.HostArch = HostArch ?? HostInformation.HostArch;
            this.TargetArch = TargetArch ?? HostInformation.HostArch;
        }

        public string Name => UseClangCl ? "clang-cl" : "msvc";
        public Version Version => new Version(VSVersion, 0);
        public ICompiler Compiler => UseClangCl ? ClangCLCC! : CLCC!;
        public ILinker Linker => LINK!;
        public IArchiver Archiver => LINK!;
        public string BuildTempPath => Directory.CreateDirectory(Path.Combine(SourceLocation.BuildTempPath, this.Version.ToString())).FullName;

        internal void FindVCVars()
        {
            if (!OperatingSystem.IsWindows())
                return;

            Log.Information("VisualStudio version ... {VSVersion}", VSVersion);
            if (VSVersion == 2022)
            {
                var vsInstance = SearchVS2022.FindBestInstance();
                
                if (vsInstance != null && vsInstance.IsValid)
                {
                    // 重要：VS 批处理文件期望 VSINSTALLDIR 以斜杠结尾
                    VSInstallDir = vsInstance.InstallPath;
                    if (!VSInstallDir!.EndsWith("/") && !VSInstallDir.EndsWith("\\"))
                    {
                        VSInstallDir = VSInstallDir.Replace("\\", "/") + "/";
                    }
                    else
                    {
                        VSInstallDir = VSInstallDir.Replace("\\", "/");
                    }
                    
                    VCVarsAllBat = vsInstance.VCVarsAllBat;
                    VCVarsBat = vsInstance.VCVarsBat;
                    WindowsSDKBat = vsInstance.WindowsSDKBat;
                    
                    Log.Verbose("Found VS2022 at: {InstallDir}", VSInstallDir);
                    if (FastFind)
                    {
                        Log.Verbose("Found VCVarsBat: {VCVarsBat}", VCVarsBat);
                        Log.Verbose("Found WindowsSDKBat: {WindowsSDKBat}", WindowsSDKBat);
                    }
                    else
                    {
                        Log.Verbose("Found VCVarsAllBat: {VCVarsAllBat}", VCVarsAllBat);
                    }
                }
                else
                {
                    Log.Error("Visual Studio 2022 not found");
                }
            }
            else
            {
                Log.Error("VS Version not supported!");
            }
        }

        static readonly Dictionary<Architecture, string> archStringMap = new Dictionary<Architecture, string> { { Architecture.X86, "x86" }, { Architecture.X64, "x64" }, { Architecture.ARM64, "arm64" } };

        private bool IsInVisualStudio()
        {
            var vsEdition = Environment.GetEnvironmentVariable("VisualStudioEdition");
            bool IsVS = !string.IsNullOrEmpty(vsEdition);
            return IsVS;
        }

        private bool IsInDeveloperPrompt()
        {
            // 检查关键的 VS Developer Prompt 环境变量
            // 这些变量只有在真正的 Developer Prompt 中才会同时存在
            var devPromptArch = Environment.GetEnvironmentVariable("VSCMD_ARG_TGT_ARCH");
            var vsInstallDir = Environment.GetEnvironmentVariable("VSINSTALLDIR");
            var vcInstallDir = Environment.GetEnvironmentVariable("VCINSTALLDIR");
            var vscmdVer = Environment.GetEnvironmentVariable("VSCMD_VER");

            // 确保是真正的 Developer Prompt，而不仅仅是安装了 VS
            bool IsPrompt = !string.IsNullOrEmpty(devPromptArch) &&
                   !string.IsNullOrEmpty(vsInstallDir) &&
                   !string.IsNullOrEmpty(vcInstallDir) &&
                   !string.IsNullOrEmpty(vscmdVer);
            return IsPrompt;
        }

        private void FindToolsInCurrentEnvironment()
        {
            var pathEnv = Environment.GetEnvironmentVariable("Path") ?? "";
            var paths = pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries);

            foreach (var path in paths)
            {
                if (!Directory.Exists(path))
                    continue;

                try
                {
                    foreach (var file in Directory.EnumerateFiles(path))
                    {
                        var fileName = Path.GetFileName(file);
                        switch (fileName.ToLowerInvariant())
                        {
                            case "cl.exe":
                                CLCCPath = file;
                                break;
                            case "link.exe":
                                LINKPath = file;
                                break;
                            case "clang-cl.exe":
                                ClangCLPath = file;
                                break;
                            case "rc.exe":
                                RCPath = file;
                                break;
                        }
                    }
                }
                catch
                {
                    // 忽略无法访问的目录
                }
            }
        }

        // Resolve the Windows-canonical temp directory, independent of the
        // current shell's TEMP/TMP env (git-bash / MSYS2 rewrite these to
        // posix paths like /tmp, which would otherwise push the vcvars dump
        // files to inconsistent locations per shell and break the offline
        // cache / make build state drift between runs).
        internal static string GetCanonicalWindowsTempDir()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                var winTemp = Path.Combine(localAppData, "Temp");
                if (Directory.Exists(winTemp))
                    return winTemp;
            }
            // Fallback: %SystemRoot%\Temp is always present on Windows.
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            return Path.Combine(systemRoot, "Temp");
        }

        // Sanitize the subprocess environment before spawning cmd.exe for the
        // vcvars capture. Goal: identical oldEnv / newEnv content regardless
        // of which shell launched SB (cmd, PowerShell, git-bash, MSYS2, …).
        private static void SanitizeEnvForVCVarsCapture(ProcessStartInfo info)
        {
            var env = info.EnvironmentVariables;
            var canonicalTemp = GetCanonicalWindowsTempDir();
            env["TEMP"] = canonicalTemp;
            env["TMP"] = canonicalTemp;

            // Drop POSIX-shell artifacts that git-bash / MSYS2 inject. These
            // are harmless at runtime but would otherwise leak into the
            // captured env dumps and invalidate the dependency fingerprints
            // whenever the user switches shells.
            string[] bashy = {
                "MSYSTEM", "MSYSTEM_PREFIX", "MSYSTEM_CARCH", "MSYSTEM_CHOST",
                "MINGW_PREFIX", "MINGW_CHOST", "MINGW_PACKAGE_PREFIX",
                "SHELL", "SHLVL", "TERM", "TERM_PROGRAM", "TERM_PROGRAM_VERSION",
                "PS1", "PS2", "PS4", "BASH", "BASH_ENV", "BASH_EXECUTION_STRING",
                "OSTYPE", "HOSTTYPE", "MACHTYPE",
                "EXEPATH", "MSYS2_PATH_TYPE", "MSYS2_ENV_CONV_EXCL",
                "PWD", "OLDPWD", "ORIGINAL_PATH", "ORIGINAL_TEMP", "ORIGINAL_TMP",
                "PROFILEREAD", "LOGNAME", "USER", "HOME", "_",
            };
            foreach (var key in bashy)
            {
                if (env.ContainsKey(key))
                    env.Remove(key);
            }
        }

        internal void RunBat(string oldEnvPath, string newEnvPath)
        {
            Process cmd = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true,
                    UseShellExecute = false
                }
            };
            SanitizeEnvForVCVarsCapture(cmd.StartInfo);
            if (FastFind)
            {
                cmd.StartInfo.Environment.Add("VSCMD_ARG_HOST_ARCH", archStringMap[HostArch]);
                cmd.StartInfo.Environment.Add("VSCMD_ARG_TGT_ARCH", archStringMap[TargetArch]);
                cmd.StartInfo.Environment.Add("VSCMD_ARG_APP_PLAT", "Desktop");
                cmd.StartInfo.Environment.Add("VSINSTALLDIR", VSInstallDir!.Replace("/", "\\"));
                
                // 强制使用最新的工具链版本
                var latestToolsetVersion = FindLatestToolsetVersion();
                if (!string.IsNullOrEmpty(latestToolsetVersion))
                {
                    cmd.StartInfo.Environment.Add("VSCMD_ARG_VCVARS_VER", latestToolsetVersion);
                    Log.Information("Forcing toolset version to latest: {Version}", latestToolsetVersion);
                }
                
                cmd.StartInfo.Arguments = $"/c set > \"{oldEnvPath}\" && \"{VCVarsBat}\" && \"{WindowsSDKBat}\" && set > \"{newEnvPath}\"";
            }
            else
            {
                string ArchString = (TargetArch == HostArch) ? archStringMap[TargetArch] : $"{archStringMap[HostArch]}_{archStringMap[TargetArch]}";
                cmd.StartInfo.Arguments = $"/c set > \"{oldEnvPath}\" && \"{VCVarsAllBat}\" {ArchString} && set > \"{newEnvPath}\"";
            }
            cmd.Start();
            cmd.WaitForExit();
        }

        internal void RunVCVars()
        {
            // Pin dump files to a Windows-canonical path rather than
            // Path.GetTempPath(), which honours TEMP/TMP — those get rewritten
            // to posix paths like /tmp under git-bash, scattering the dumps
            // across different locations per shell and polluting state.
            var sbTempDir = Path.Combine(GetCanonicalWindowsTempDir(), "SB");
            Directory.CreateDirectory(sbTempDir);
            var oldEnvPath = Path.Combine(sbTempDir, $"vcvars_{VSVersion}_prev_{HostArch}_{TargetArch}.txt");
            var newEnvPath = Path.Combine(sbTempDir, $"vcvars_{VSVersion}_post_{HostArch}_{TargetArch}.txt");

            // 检测是否已在 Developer Prompt 中
            if (IsInDeveloperPrompt())
            {
                Log.Information("Already in Visual Studio Developer Command Prompt, using existing environment");

                // 直接使用当前环境变量
                // OrdinalIgnoreCase to match Windows env semantics — guards against
                // shells (git-bash / MSYS2) that store PATH under an uppercase key.
                VCEnvVariables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    VCEnvVariables[entry.Key.ToString()!] = entry.Value?.ToString();
                }

                // 从当前环境中查找工具
                FindToolsInCurrentEnvironment();

                // 验证是否找到必要的工具
                if (string.IsNullOrEmpty(CLCCPath) || !File.Exists(CLCCPath))
                {
                    Log.Warning("cl.exe not found in Developer Prompt environment");
                }
                if (string.IsNullOrEmpty(LINKPath) || !File.Exists(LINKPath))
                {
                    Log.Warning("link.exe not found in Developer Prompt environment");
                }
                if (string.IsNullOrEmpty(RCPath) || !File.Exists(RCPath))
                {
                    Log.Warning("rc.exe not found in Developer Prompt environment");
                }
            }
            else
            {
                // VS 下配了一部分的环境变量，所以不能乱剔除
                bool isInVisualStudio = IsInVisualStudio();

                // 不在 Developer Prompt 中，执行原有的初始化逻辑
                RunBat(oldEnvPath, newEnvPath);

                var oldEnv = EnvReader.Load(oldEnvPath)!;
                VCEnvVariables = EnvReader.Load(newEnvPath)!;
                // Preprocess: cull old env variables
                if (!isInVisualStudio)
                {
                    foreach (var oldVar in oldEnv)
                    {
                        if (VCEnvVariables.ContainsKey(oldVar.Key) && VCEnvVariables[oldVar.Key] == oldEnv[oldVar.Key])
                            VCEnvVariables.Remove(oldVar.Key);
                    }
                }
                // Preprocess: cull user env variables
                var vcPaths = VCEnvVariables["Path"]!.Split(';').ToHashSet();
                var oldPaths = oldEnv["Path"].Split(';').ToHashSet();
                if (!isInVisualStudio)
                {
                    vcPaths.ExceptWith(oldPaths);
                }
                VCEnvVariables["Path"] = string.Join(";", vcPaths);
                // Preprocess: calculate include dir
                var OriginalIncludes = VCEnvVariables.TryGetValue("INCLUDE", out var V0) ? V0 : "";
                var VCVarsIncludes = VCEnvVariables.TryGetValue("__VSCMD_VCVARS_INCLUDE", out var V1) ? V1 : "";
                var WindowsSDKIncludes = VCEnvVariables.TryGetValue("__VSCMD_WINSDK_INCLUDE", out var V2) ? V2 : "";
                var NetFXIncludes = VCEnvVariables.TryGetValue("__VSCMD_NETFX_INCLUDE", out var V3) ? V3 : "";
                var fallbackWindowsSdkVersion = FindLatestInstalledWindowsSdkVersion();
                if (string.IsNullOrWhiteSpace(WindowsSDKIncludes) && !string.IsNullOrEmpty(fallbackWindowsSdkVersion))
                {
                    WindowsSDKIncludes = BuildWindowsSdkIncludePaths(fallbackWindowsSdkVersion);
                    if (!string.IsNullOrEmpty(WindowsSDKIncludes))
                    {
                        Log.Warning("Windows SDK include paths are missing from vcvars output, falling back to installed kit {FallbackVersion}", fallbackWindowsSdkVersion);
                        VCEnvVariables["__VSCMD_WINSDK_INCLUDE"] = WindowsSDKIncludes;
                    }
                }
                VCEnvVariables["INCLUDE"] = JoinEnvPaths(VCVarsIncludes, WindowsSDKIncludes, NetFXIncludes, OriginalIncludes);

                var originalLib = (VCEnvVariables.TryGetValue("LIB", out var V4) ? V4 : null) ?? "";
                if ((!string.IsNullOrEmpty(fallbackWindowsSdkVersion)) && !originalLib.Contains(@"\Windows Kits\10\Lib\", StringComparison.OrdinalIgnoreCase))
                {
                    var windowsSdkLibs = BuildWindowsSdkLibPaths(fallbackWindowsSdkVersion);
                    if (!string.IsNullOrEmpty(windowsSdkLibs))
                    {
                        Log.Warning("Windows SDK library paths are missing from vcvars output, falling back to installed kit {FallbackVersion}", fallbackWindowsSdkVersion);
                        VCEnvVariables["LIB"] = JoinEnvPaths(originalLib, windowsSdkLibs);
                    }
                }
                // Enum all files and pick usable tools
                foreach (var path in vcPaths)
                {
                    if (!Directory.Exists(path))
                        continue;

                    foreach (var file in Directory.EnumerateFiles(path))
                    {
                        if (Path.GetFileName(file) == "cl.exe" && file.Contains("MSVC"))
                            CLCCPath = file;
                        if (Path.GetFileName(file) == "link.exe" && file.Contains("MSVC"))
                            LINKPath = file;
                        if (Path.GetFileName(file) == "clang-cl.exe")
                            ClangCLPath = file;
                        if (Path.GetFileName(file) == "rc.exe")
                            RCPath = file;
                    }
                }

                // 如果在标准路径中没有找到link.exe，尝试更广泛的搜索
                if (string.IsNullOrEmpty(LINKPath))
                {
                    LINKPath = FindLinkerInAlternativePaths(vcPaths);
                }
                // clang-cl may be installed in a different user path
                if (!File.Exists(ClangCLPath))
                {
                    foreach (var path in oldPaths)
                    {
                        if (!Directory.Exists(path))
                            continue;

                        foreach (var file in Directory.EnumerateFiles(path))
                        {
                            if (Path.GetFileName(file) == "clang-cl.exe")
                                ClangCLPath = file;
                        }
                    }
                }
            }

            NormalizeWindowsSdkFingerprintVariables();

            var WindowsSDKVersion = VCEnvVariables["WindowsSDKVersion"]!.Replace('\\', ' ');
            var WindowsSDKLibVersion = VCEnvVariables["WindowsSDKLibVersion"]!.Replace('\\', ' ');
            var UCRTVersion = VCEnvVariables["UCRTVersion"]!.Replace('\\', ' ');
            Log.Information("WindowsSDKVersion version ... {WindowsSDKVersion}", WindowsSDKVersion);
            Log.Verbose("WindowsSDKLibVersion version ... {WindowsSDKLibVersion}", WindowsSDKLibVersion);
            Log.Verbose("UCRTVersion version ... {UCRTVersion}", UCRTVersion);

            if (!string.IsNullOrEmpty(CLCCPath))
                CLCC = new CLCompiler(CLCCPath!, VCEnvVariables);
            if (!string.IsNullOrEmpty(LINKPath))
                LINK = new LINK(LINKPath!, VCEnvVariables);
            if (!string.IsNullOrEmpty(ClangCLPath))
                ClangCLCC = new ClangCLCompiler(ClangCLPath!, VCEnvVariables);
            if (!string.IsNullOrEmpty(RCPath))
                RC = new RCCompiler(RCPath!, VCEnvVariables);

            if (CLCC is null && !UseClangCl)
                Log.Fatal("CL.exe tool not found, please ensure Visual Studio is installed correctly.");
            if (ClangCLCC is null && UseClangCl)
                Log.Fatal("ClangCLCC tool not found, please ensure Clang is installed correctly.");
            if (LINK is null)
                Log.Fatal("LINK tool not found, please ensure Visual Studio is installed correctly.");
        }

        public readonly int VSVersion;
        public readonly bool UseClangCl;
        public readonly WindowsSDKStrategy WindowsSDKStrategy;
        public readonly Architecture HostArch;
        public readonly Architecture TargetArch;

        public string? VSInstallDir { get; private set; }
        public string? VCVarsAllBat { get; private set; }
        public string? VCVarsBat { get; private set; }
        public string? WindowsSDKBat { get; private set; }

        public Dictionary<string, string?>? VCEnvVariables { get; private set; }
        public CLCompiler? CLCC { get; private set; }
        public ClangCLCompiler? ClangCLCC { get; private set; }
        public LINK? LINK { get; private set; }
        public RCCompiler? RC { get; private set; }
        public string? CLCCPath { get; private set; }
        public string? ClangCLPath { get; private set; }
        public string? LINKPath { get; private set; }
        public string? RCPath { get; private set; }

        /// <summary>
        /// 在备用路径中查找链接器
        /// </summary>
        private string FindLinkerInAlternativePaths(IEnumerable<string> vcPaths)
        {
            var searchPaths = new List<string>();
            
            // 添加VC路径（移除MSVC限制，允许在任何VC路径中查找）
            foreach (var path in vcPaths)
            {
                if (Directory.Exists(path))
                {
                    searchPaths.Add(path);
                }
            }
            
            // 添加常见的Visual Studio工具链路径
            var commonToolchainPaths = new[]
            {
                @"C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Tools\MSVC",
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC",
                @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Tools\MSVC",
                @"C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\VC\Tools\MSVC",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Tools\MSVC",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\VC\Tools\MSVC",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Tools\MSVC"
            };
            
            foreach (var basePath in commonToolchainPaths)
            {
                if (Directory.Exists(basePath))
                {
                    try
                    {
                        var versionDirs = Directory.GetDirectories(basePath);
                        foreach (var versionDir in versionDirs)
                        {
                            searchPaths.Add(Path.Combine(versionDir, "bin", "Hostx64", "x64"));
                            searchPaths.Add(Path.Combine(versionDir, "bin", "Hostx86", "x86"));
                        }
                    }
                    catch
                    {
                        // 忽略访问异常
                    }
                }
            }
            
            // 查找link.exe
            foreach (var searchPath in searchPaths.Where(Directory.Exists).Distinct())
            {
                var linkPath = Path.Combine(searchPath, "link.exe");
                if (File.Exists(linkPath))
                {
                    Log.Information("Found LINK.exe at alternative path: {LinkPath}", linkPath);
                    return linkPath;
                }
            }
            
            return "";
        }


        #region HelpersForTools
        public static bool IsValidRT(string what) => ValidRuntimeArguments.Contains(what);
        private static readonly string[] ValidRuntimeArguments = ["MT", "MTd", "MD", "MDd"];
        #endregion

        #region ToolsetVersion
        /// <summary>
        /// 查找最新的工具链版本
        /// </summary>
        private string FindLatestToolsetVersion()
        {
            try
            {
                if (string.IsNullOrEmpty(VSInstallDir))
                    return "";

                var toolsDir = Path.Combine(VSInstallDir, "VC", "Tools", "MSVC");
                if (!Directory.Exists(toolsDir))
                    return "";

                // 查找所有工具链版本目录
                var versions = new List<Version>();
                foreach (var dir in Directory.GetDirectories(toolsDir))
                {
                    var dirName = Path.GetFileName(dir);
                    if (Version.TryParse(dirName, out var version))
                    {
                        // 检查是否包含必要的工具文件
                        var clExePath = Path.Combine(dir, "bin", "Hostx64", "x64", "cl.exe");
                        if (File.Exists(clExePath))
                        {
                            versions.Add(version);
                        }
                    }
                }

                if (versions.Count == 0)
                    return "";

                // 返回最新版本
                var latestVersion = versions.OrderByDescending(v => v).First();
                return latestVersion.ToString();
            }
            catch (Exception ex)
            {
                Log.Verbose("Failed to find latest toolset version: {Message}", ex.Message);
                return "";
            }
        }

        private void NormalizeWindowsSdkFingerprintVariables()
        {
            static string NormalizeVersionValue(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return "";
                return value.Trim().TrimEnd('\\', '/');
            }

            static string WithTrailingBackslash(string value)
            {
                return string.IsNullOrEmpty(value) ? "" : value.TrimEnd('\\', '/') + "\\";
            }

            static bool IsUsableWindowsSdkVersion(string value)
            {
                return Version.TryParse(value, out _);
            }

            string windowsSdkVersion = VCEnvVariables!.TryGetValue("WindowsSDKVersion", out var sdkVersionValue)
                ? NormalizeVersionValue(sdkVersionValue)
                : "";
            string windowsSdkLibVersion = VCEnvVariables.TryGetValue("WindowsSDKLibVersion", out var sdkLibVersionValue)
                ? NormalizeVersionValue(sdkLibVersionValue)
                : "";
            string ucrtVersion = VCEnvVariables.TryGetValue("UCRTVersion", out var ucrtVersionValue)
                ? NormalizeVersionValue(ucrtVersionValue)
                : "";

            string fallbackVersion = FindLatestInstalledWindowsSdkVersion();
            if (!IsUsableWindowsSdkVersion(windowsSdkVersion) && !string.IsNullOrEmpty(fallbackVersion))
            {
                Log.Warning("WindowsSDKVersion is missing or invalid ('{WindowsSDKVersion}'), falling back to installed kit {FallbackVersion}", windowsSdkVersion, fallbackVersion);
                windowsSdkVersion = fallbackVersion;
            }
            if (!IsUsableWindowsSdkVersion(windowsSdkLibVersion) && !string.IsNullOrEmpty(fallbackVersion))
            {
                Log.Warning("WindowsSDKLibVersion is missing or invalid ('{WindowsSDKLibVersion}'), falling back to installed kit {FallbackVersion}", windowsSdkLibVersion, fallbackVersion);
                windowsSdkLibVersion = fallbackVersion;
            }
            if (!IsUsableWindowsSdkVersion(ucrtVersion) && !string.IsNullOrEmpty(windowsSdkVersion))
            {
                Log.Warning("UCRTVersion is missing or invalid ('{UCRTVersion}'), falling back to Windows SDK version {FallbackVersion}", ucrtVersion, windowsSdkVersion);
                ucrtVersion = windowsSdkVersion;
            }

            VCEnvVariables["WindowsSDKVersion"] = WithTrailingBackslash(windowsSdkVersion);
            VCEnvVariables["WindowsSDKLibVersion"] = WithTrailingBackslash(windowsSdkLibVersion);
            VCEnvVariables["UCRTVersion"] = WithTrailingBackslash(ucrtVersion);
        }

        private static string FindLatestInstalledWindowsSdkVersion()
        {
            try
            {
                var kitsLibDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Lib");
                if (!Directory.Exists(kitsLibDir))
                    return "";

                var versions = Directory.GetDirectories(kitsLibDir)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name) && Version.TryParse(name, out _))
                    .Select(name => Version.Parse(name!))
                    .OrderByDescending(v => v)
                    .ToList();

                return versions.Count > 0 ? versions[0].ToString() : "";
            }
            catch (Exception ex)
            {
                Log.Verbose("Failed to find latest Windows SDK version: {Message}", ex.Message);
                return "";
            }
        }

        private static string JoinEnvPaths(params string?[] segments)
        {
            return string.Join(";", segments
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .Select(segment => segment!.Trim().TrimEnd(';')));
        }

        private static string BuildWindowsSdkIncludePaths(string version)
        {
            var kitsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
            var includeRoot = Path.Combine(kitsRoot, "Include", version);
            if (!Directory.Exists(includeRoot))
                return "";

            var dirs = new[]
            {
                Path.Combine(includeRoot, "ucrt"),
                Path.Combine(includeRoot, "shared"),
                Path.Combine(includeRoot, "um"),
                Path.Combine(includeRoot, "winrt"),
                Path.Combine(includeRoot, "cppwinrt"),
            }.Where(Directory.Exists).ToArray();

            return JoinEnvPaths(dirs);
        }

        private string BuildWindowsSdkLibPaths(string version)
        {
            var kitsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
            var libRoot = Path.Combine(kitsRoot, "Lib", version);
            if (!Directory.Exists(libRoot))
                return "";

            var arch = archStringMap[TargetArch];
            var dirs = new[]
            {
                Path.Combine(libRoot, "ucrt", arch),
                Path.Combine(libRoot, "um", arch),
            }.Where(Directory.Exists).ToArray();

            return JoinEnvPaths(dirs);
        }
        #endregion
    }

    public class VisualStudioSetup : ISetup
    {
        public bool UseClangCl { get; set; } = true;
        public WindowsSDKStrategy WindowsSDKStrategy { get; set; } = WindowsSDKStrategy.Default;
        public VisualStudio VisualStudio => _visualStudio ??= new VisualStudio(2022, UseClangCl, WindowsSDKStrategy);

        public void Setup(BuildInstance Instance)
        {
            if (!OperatingSystem.IsWindows())
                return;
                
            if (Instance.TargetOS == OSPlatform.Windows && BuildInstance.HostOS == OSPlatform.Windows)
            {
                using (Profiler.BeginZone("InitializeVisualStudio", color: (uint)Profiler.ColorType.WebMaroon))
                {
                    Stopwatch sw = Stopwatch.StartNew();

                    VisualStudio.FindVCVars();
                    sw.Stop();
                    Log.Information("Find VCVars took {ElapsedMilliseconds}s", sw.ElapsedMilliseconds / 1000.0f);
                    sw.Restart();
                    VisualStudio.RunVCVars();
                    sw.Stop();
                    Log.Information("Run VCVars took {ElapsedMilliseconds}s", sw.ElapsedMilliseconds / 1000.0f);
                }
            }
        }

        private VisualStudio? _visualStudio;
    }
}
