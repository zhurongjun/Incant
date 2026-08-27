using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Win32;
using Serilog;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace SB.Core
{
    [SupportedOSPlatform("windows")]
    public class SearchVS2022
    {
        public class VSInstance
        {
            public string? InstallPath { get; set; }
            public string? InstanceId { get; set; }
            public string? Edition { get; set; }
            public string? Version { get; set; }
            public string? VCVarsAllBat { get; set; }
            public string? VCVarsBat { get; set; }
            public string? WindowsSDKBat { get; set; }
            public bool IsValid => !string.IsNullOrEmpty(InstallPath) && Directory.Exists(InstallPath);
        }

        private static readonly ILogger Log = Serilog.Log.ForContext<SearchVS2022>();

        /// <summary>
        /// 从注册表查找 VS2022 安装实例
        /// </summary>
        public static List<VSInstance> FindFromRegistry()
        {
            var instances = new List<VSInstance>();

            try
            {
                // 查找主要的 VS2022 实例注册表项
                var registryPaths = new[]
                {
                    @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio",
                    @"SOFTWARE\Microsoft\VisualStudio",
                    @"SOFTWARE\WOW6432Node\Microsoft\DevDiv\vs\Servicing\17.0",
                    @"SOFTWARE\Microsoft\DevDiv\vs\Servicing\17.0"
                };

                foreach (var path in registryPaths)
                {
                    try
                    {
                        using var key = Registry.LocalMachine.OpenSubKey(path);
                        if (key == null) continue;

                        foreach (var subKeyName in key.GetSubKeyNames())
                        {
                            // VS2022 的版本号以 17 开头
                            if (!subKeyName.StartsWith("17.") && !subKeyName.Contains("_")) continue;

                            using var subKey = key.OpenSubKey(subKeyName);
                            if (subKey == null) continue;

                            var installPath = subKey.GetValue("InstallLocation") as string ??
                                            subKey.GetValue("ProductDir") as string;

                            if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                            {
                                var instance = new VSInstance
                                {
                                    InstallPath = Path.GetFullPath(installPath),
                                    InstanceId = subKeyName,
                                    Version = subKey.GetValue("Version") as string ?? "17.0"
                                };

                                // 查找批处理文件
                                FindBatchFiles(instance);

                                if (instance.IsValid)
                                {
                                    instances.Add(instance);
                                    Log.Verbose("Found VS2022 from registry: {InstallPath}", instance.InstallPath);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Verbose("Registry search failed for path {Path}: {Message}", path, ex.Message);
                    }
                }

                // 查找卸载信息中的 VS2022
                FindFromUninstallRegistry(instances);
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to search registry for VS2022: {Message}", ex.Message);
            }

            return instances;
        }

        /// <summary>
        /// 从卸载注册表查找 VS2022
        /// </summary>
        private static void FindFromUninstallRegistry(List<VSInstance> instances)
        {
            var uninstallPaths = new[]
            {
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var path in uninstallPaths)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(path);
                    if (key == null) continue;

                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey == null) continue;

                        var displayName = subKey.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(displayName) || !displayName.Contains("Visual Studio") || !displayName.Contains("2022"))
                            continue;

                        var installLocation = subKey.GetValue("InstallLocation") as string;
                        if (string.IsNullOrEmpty(installLocation) || !Directory.Exists(installLocation))
                            continue;

                        // 检查是否已存在
                        if (instances.Any(i => i.InstallPath == installLocation))
                            continue;

                        var instance = new VSInstance
                        {
                            InstallPath = Path.GetFullPath(installLocation),
                            InstanceId = subKeyName,
                            Edition = ExtractEdition(displayName),
                            Version = subKey.GetValue("DisplayVersion") as string ?? "17.0"
                        };

                        FindBatchFiles(instance);

                        if (instance.IsValid)
                        {
                            instances.Add(instance);
                            Log.Verbose("Found VS2022 from uninstall registry: {InstallPath}", instance.InstallPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Verbose("Uninstall registry search failed for path {Path}: {Message}", path, ex.Message);
                }
            }
        }

        /// <summary>
        /// 使用 vswhere 查找 VS2022 安装实例
        /// </summary>
        public static List<VSInstance> FindFromVSWhere()
        {
            var instances = new List<VSInstance>();

            try
            {
                // vswhere 可能的位置
                var vswherePaths = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                               "Microsoft Visual Studio", "Installer", "vswhere.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                               "Microsoft Visual Studio", "Installer", "vswhere.exe"),
                    "vswhere.exe" // 如果在 PATH 中
                };

                string? vswherePath = vswherePaths.FirstOrDefault(File.Exists);

                if (string.IsNullOrEmpty(vswherePath))
                {
                    Log.Verbose("vswhere.exe not found");
                    return instances;
                }

                Log.Verbose("Using vswhere at: {Path}", vswherePath);

                // 执行 vswhere 查找 VS2022 (版本 17.x)
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = vswherePath,
                        Arguments = "-version \"[17.0,18.0)\" -format json -utf8 -nologo",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrEmpty(output))
                {
                    var jsonInstances = JsonSerializer.Deserialize<List<VSWhereInstance>>(output);
                    if (jsonInstances != null)
                    {
                        foreach (var jsonInstance in jsonInstances)
                        {
                            if (string.IsNullOrEmpty(jsonInstance.installationPath)) continue;

                            var instance = new VSInstance
                            {
                                InstallPath = Path.GetFullPath(jsonInstance.installationPath),
                                InstanceId = jsonInstance.instanceId,
                                Edition = jsonInstance.installationName,
                                Version = jsonInstance.installationVersion
                            };

                            FindBatchFiles(instance);

                            if (instance.IsValid)
                            {
                                instances.Add(instance);
                                Log.Verbose("Found VS2022 from vswhere: {InstallPath}", instance.InstallPath);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to use vswhere: {Message}", ex.Message);
            }

            return instances;
        }

        /// <summary>
        /// 从文件系统查找 VS2022（改进版，避免硬编码）
        /// </summary>
        public static List<VSInstance> FindFromFileSystem()
        {
            var instances = new List<VSInstance>();

            try
            {
                // 获取所有可能的 Program Files 路径
                var programFilesPaths = GetProgramFilesPaths();

                foreach (var programFiles in programFilesPaths)
                {
                    var vsBasePath = Path.Combine(programFiles, "Microsoft Visual Studio", "2022");

                    if (!Directory.Exists(vsBasePath))
                        continue;

                    // 查找所有版本（Community, Professional, Enterprise, Preview）
                    foreach (var editionDir in Directory.GetDirectories(vsBasePath))
                    {
                        var instance = new VSInstance
                        {
                            InstallPath = Path.GetFullPath(editionDir),
                            Edition = Path.GetFileName(editionDir),
                            Version = "17.0"
                        };

                        FindBatchFiles(instance);

                        if (instance.IsValid)
                        {
                            instances.Add(instance);
                            Log.Verbose("Found VS2022 from file system: {InstallPath}", instance.InstallPath);
                        }
                    }
                }

                // 也搜索所有逻辑驱动器（作为后备方案）
                if (instances.Count == 0)
                {
                    instances.AddRange(FindFromAllDrives());
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to search file system: {Message}", ex.Message);
            }

            return instances;
        }

        /// <summary>
        /// 从所有驱动器查找（优化版）
        /// </summary>
        private static List<VSInstance> FindFromAllDrives()
        {
            var instances = new List<VSInstance>();
            var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady);

            foreach (var drive in drives)
            {
                var searchPaths = new[]
                {
                    Path.Combine(drive.RootDirectory.FullName, "Program Files", "Microsoft Visual Studio", "2022"),
                    Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "Microsoft Visual Studio", "2022")
                };

                foreach (var searchPath in searchPaths)
                {
                    if (!Directory.Exists(searchPath))
                        continue;

                    try
                    {
                        foreach (var editionDir in Directory.GetDirectories(searchPath))
                        {
                            var instance = new VSInstance
                            {
                                InstallPath = Path.GetFullPath(editionDir),
                                Edition = Path.GetFileName(editionDir),
                                Version = "17.0"
                            };

                            FindBatchFiles(instance);

                            if (instance.IsValid && !instances.Any(i => i.InstallPath == instance.InstallPath))
                            {
                                instances.Add(instance);
                                Log.Verbose("Found VS2022 from drive {Drive}: {InstallPath}", drive.Name, instance.InstallPath);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Verbose("Failed to search {Path}: {Message}", searchPath, ex.Message);
                    }
                }
            }

            return instances;
        }

        /// <summary>
        /// 查找批处理文件
        /// </summary>
        private static void FindBatchFiles(VSInstance instance)
        {
            if (string.IsNullOrEmpty(instance.InstallPath) || !Directory.Exists(instance.InstallPath))
                return;

            try
            {
                var matcher = new Matcher();
                matcher.AddIncludePatterns(new[]
                {
                    "**/vcvarsall.bat",
                    "**/vsdevcmd/ext/vcvars.bat",
                    "**/vsdevcmd/core/winsdk.bat"
                });

                var files = matcher.GetResultsInFullPath(instance.InstallPath);

                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    switch (fileName.ToLowerInvariant())
                    {
                        case "vcvarsall.bat":
                            instance.VCVarsAllBat = file;
                            break;
                        case "vcvars.bat":
                            instance.VCVarsBat = file;
                            break;
                        case "winsdk.bat":
                            instance.WindowsSDKBat = file;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Verbose("Failed to find batch files in {Path}: {Message}", instance.InstallPath, ex.Message);
            }
        }

        /// <summary>
        /// 获取所有可能的 Program Files 路径
        /// </summary>
        private static List<string> GetProgramFilesPaths()
        {
            var paths = new HashSet<string>();

            // 标准环境变量
            var envPaths = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramW6432")
            };

            foreach (var path in envPaths)
            {
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    paths.Add(Path.GetFullPath(path));
                }
            }

            return paths.ToList();
        }

        /// <summary>
        /// 从显示名称提取版本
        /// </summary>
        private static string ExtractEdition(string displayName)
        {
            if (displayName.Contains("Community")) return "Community";
            if (displayName.Contains("Professional")) return "Professional";
            if (displayName.Contains("Enterprise")) return "Enterprise";
            if (displayName.Contains("Preview")) return "Preview";
            return "Unknown";
        }

        /// <summary>
        /// 综合查找 VS2022，按优先级返回最佳实例
        /// </summary>
        public static VSInstance? FindBestInstance()
        {
            Log.Information("Searching for Visual Studio 2022...");

            var allInstances = new List<VSInstance>();

            // 1. 首先尝试 vswhere（最可靠）
            var vswhereInstances = FindFromVSWhere();
            allInstances.AddRange(vswhereInstances);
            Log.Verbose("Found {Count} instances from vswhere", vswhereInstances.Count);

            // 2. 从注册表查找
            if (allInstances.Count == 0)
            {
                var registryInstances = FindFromRegistry();
                allInstances.AddRange(registryInstances);
                Log.Verbose("Found {Count} instances from registry", registryInstances.Count);
            }

            // 3. 从文件系统查找（作为后备）
            if (allInstances.Count == 0)
            {
                var fileSystemInstances = FindFromFileSystem();
                allInstances.AddRange(fileSystemInstances);
                Log.Verbose("Found {Count} instances from file system", fileSystemInstances.Count);
            }

            // 去重
            var uniqueInstances = allInstances
                .GroupBy(i => i.InstallPath?.ToLowerInvariant())
                .Select(g => g.First())
                .ToList();

            if (uniqueInstances.Count == 0)
            {
                Log.Warning("No Visual Studio 2022 installation found");
                return null;
            }

            // 选择最佳实例（优先选择有批处理文件的）
            var bestInstance = uniqueInstances
                .OrderByDescending(i => !string.IsNullOrEmpty(i.VCVarsBat) && !string.IsNullOrEmpty(i.WindowsSDKBat))
                .ThenByDescending(i => !string.IsNullOrEmpty(i.VCVarsAllBat))
                .ThenByDescending(i => i.Edition == "Enterprise")
                .ThenByDescending(i => i.Edition == "Professional")
                .ThenByDescending(i => i.Edition == "Community")
                .FirstOrDefault();

            if (bestInstance != null)
            {
                Log.Information("Selected VS2022: {Edition} at {Path}", bestInstance.Edition, bestInstance.InstallPath);
            }

            return bestInstance;
        }

        // vswhere JSON 输出结构
        private class VSWhereInstance
        {
            public string? instanceId { get; set; }
            public string? installDate { get; set; }
            public string? installationName { get; set; }
            public string? installationPath { get; set; }
            public string? installationVersion { get; set; }
            public string? productId { get; set; }
            public string? productPath { get; set; }
            public bool isPrerelease { get; set; }
        }
    }
}