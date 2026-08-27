using System.Collections.Concurrent;
using System.IO.Compression;
using System.Threading.Tasks;
using SB.Core;

namespace SB
{
    using BS = BuildInstance;
    public static class Install
    {
        public static Task<string> Tool(BuildInstance instance, string name, string destination = "", string overrideSource = "", bool platPostfix = true)
            => ToolImpl(instance, name, destination, overrideSource, platPostfix);

        public static Task SDK(BuildInstance instance, string name, Dictionary<string, string>? directoryMappings = null, bool platPostfix = true, string overrideSource = "", bool copyToBinaryDirectory = true)
            => SDKImpl(instance, name, directoryMappings, platPostfix, overrideSource, copyToBinaryDirectory);

        public static Task SDKFromUrl(BuildInstance instance, string name, string url, string fileName = "", string sha256 = "", Dictionary<string, string>? directoryMappings = null, bool copyToBinaryDirectory = true, bool force = false)
            => SDKFromUrlImpl(instance, name, url, fileName, sha256, directoryMappings, copyToBinaryDirectory, force);

        public static Task<string> File(BuildInstance instance, string name, string destination, string overrideSource = "")
            => FileImpl(instance, name, destination, overrideSource);

        private static bool IsUnixSymlinkEntry(ZipArchiveEntry entry)
        {
            // upper 16 bits: unix mode
            var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            const int S_IFMT = 0xF000;
            const int S_IFLNK = 0xA000;
            return (unixMode & S_IFMT) == S_IFLNK;
        }

        private static void RemovePathIfExists(string path)
        {
            // delete symlink first (works for broken links too)
            var fi = new FileInfo(path);
            if (fi.LinkTarget is not null)
            {
                fi.Delete();
                return;
            }

            var di = new DirectoryInfo(path);
            if (di.Exists && di.LinkTarget is not null)
            {
                di.Delete();
                return;
            }

            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, true);
        }

        private static void ExtractZipPreserveSymlinks(string zipPath, string destinationDirectory, bool overwrite)
        {
            Directory.CreateDirectory(destinationDirectory);
            var root = Path.GetFullPath(destinationDirectory);

            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                var destPath = Path.GetFullPath(Path.Combine(root, entry.FullName));

                // Directory entry
                if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(destPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                if (!OperatingSystem.IsWindows() && IsUnixSymlinkEntry(entry))
                {
                    RemovePathIfExists(destPath);

                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                    var linkTarget = reader.ReadToEnd();

                    // macOS/Linux: create symlink itself
                    System.IO.File.CreateSymbolicLink(destPath, linkTarget);
                    continue;
                }

                if (overwrite) RemovePathIfExists(destPath);
                entry.ExtractToFile(destPath, overwrite);
            }
        }

        private static async Task<string> ToolImpl(BuildInstance instance, string name, string destination = "", string overrideSource = "", bool platPostfix = true)
        {
            var buildDirs = instance.GetStage<Stages.PrepareEngineDirectoriesStage>()!;
            var buildDatabases = instance.GetStage<Stages.PrepareEngineDatabasesStage>()!;
            string toolDirectory;
            if (destination == "")
            {
                toolDirectory = buildDirs.ToolDir;
            }
            else
            {
                toolDirectory = Path.IsPathFullyQualified(destination) ? destination : Path.Combine(buildDirs.BuildDir, destination);
            }
            toolDirectory = Path.Combine(toolDirectory, name);
            await buildDatabases.Download.RunIfOutdated("Install.Tools.Download", name, "Install.Tools", async (DependencyRecord depend) =>
            {
                var zipFile = await Download.DownloadFile(instance, platPostfix ? name + GetPlatPostfix() : name + ".zip", overrideSource);

                lock (InstallLocks.GetOrAdd($"Install.Tool | {name} | Copy", _ => new System.Threading.Lock()))
                {
                    using (Profiler.BeginZone($"Install.Tool | {name} | Copy", color: (uint)Profiler.ColorType.Pink1))
                    {
                        Directory.CreateDirectory(toolDirectory);
                        ExtractZipPreserveSymlinks(zipFile, toolDirectory, true);
                        SetOwnerExecutablePermissions(toolDirectory);

                        depend.ExternalFiles.Add(zipFile);
                        depend.ExternalFiles.AddRange(Directory.GetFiles(toolDirectory, "*", SearchOption.AllDirectories));
                    }
                }
            }, null, null);
            return toolDirectory;
        }

        private static void SetOwnerExecutablePermissions(string directory)
        {
            if (OperatingSystem.IsWindows())
                return;

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                System.IO.File.SetUnixFileMode(file, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        private static async Task SDKImpl(BuildInstance instance, string name, Dictionary<string, string>? directoryMappings = null, bool platPostfix = true, string overrideSource = "", bool copyToBinaryDirectory = true)
            => await SDKFromZipImpl(
                instance,
                name,
                () => Download.DownloadFile(instance, platPostfix ? name + GetPlatPostfix() : name + ".zip", overrideSource),
                directoryMappings,
                copyToBinaryDirectory,
                null);

        private static async Task SDKFromUrlImpl(BuildInstance instance, string name, string url, string fileName = "", string sha256 = "", Dictionary<string, string>? directoryMappings = null, bool copyToBinaryDirectory = true, bool force = false)
            => await SDKFromZipImpl(
                instance,
                name,
                () => Download.DownloadFileFromUrl(instance, url, fileName, sha256, force),
                directoryMappings,
                copyToBinaryDirectory,
                new[] { "direct-url", url, fileName, sha256, force.ToString() });

        private static async Task SDKFromZipImpl(BuildInstance instance, string name, Func<Task<string>> zipFileResolver, Dictionary<string, string>? directoryMappings = null, bool copyToBinaryDirectory = true, string[]? downloadArgs = null)
        {
            var buildDirs = instance.GetStage<Stages.PrepareEngineDirectoriesStage>()!;
            var buildDatabases = instance.GetStage<Stages.PrepareEngineDatabasesStage>()!;
            var intermediateDirectory = Path.Combine(buildDirs.DownloadDir, "SDKs", name);
            await buildDatabases.Download.RunIfOutdated("Install.SDKs.Download", name, "Install.SDKs", async (DependencyRecord depend) =>
            {
                Directory.CreateDirectory(intermediateDirectory);
                var zipFile = await zipFileResolver();
                using (Profiler.BeginZone($"Install.SDKs | {name} | Download", color: (uint)Profiler.ColorType.Pink1))
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        foreach (var file in Directory.GetFiles(intermediateDirectory, "*"))
                            System.IO.File.SetUnixFileMode(file, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                    ExtractZipPreserveSymlinks(zipFile, intermediateDirectory, true);
                    depend.ExternalFiles.Add(zipFile);
                    depend.ExternalFiles.AddRange(Directory.GetFiles(intermediateDirectory, "*", SearchOption.AllDirectories));
                }
            }, null, downloadArgs);

            if (!copyToBinaryDirectory)
                return;

            var configures = instance.GetStage<Stages.LoadConfigures>()!;
            var buildDirectory = Path.Combine(buildDirs.BuildDir, $"{instance.TargetOS}-{instance.TargetArch}-{configures.ConfigurationName}");
            var args = new List<string>();
            if (directoryMappings is not null)
            {
                args.AddRange(directoryMappings.Keys);
                args.AddRange(directoryMappings.Values);
            }
            args.Add(intermediateDirectory);
            args.Add(buildDirectory);
            lock (InstallLocks.GetOrAdd($"Install.SDKs | {name} | Copy", _ => new System.Threading.Lock()))
            {
                using (Profiler.BeginZone($"Install.SDKs | {name} | Copy", color: (uint)Profiler.ColorType.Pink1))
                {
                    buildDatabases.SDK.RunIfOutdated("Install.SDK.Copy", name, "Install.SDKs", (DependencyRecord depend) =>
                    {
                        Directory.CreateDirectory(buildDirectory);

                        depend.ExternalFiles.AddRange(Directory.GetFiles(intermediateDirectory, "*", SearchOption.AllDirectories));
                        if (directoryMappings is null)
                        {
                            depend.ExternalFiles.AddRange(DirectoryCopy(intermediateDirectory, buildDirectory, true));
                        }
                        else
                        {
                            foreach (var mapping in directoryMappings)
                            {
                                var source = Path.Combine(intermediateDirectory, mapping.Key);
                                string destination;
                                if (Path.IsPathFullyQualified(mapping.Value))
                                    destination = mapping.Value;
                                else
                                    destination = Path.Combine(buildDirectory, mapping.Value);
                                depend.ExternalFiles.AddRange(DirectoryCopy(source, destination, true));
                            }
                        }
                    }, null, args);
                }
            }
        }

        private static async Task<string> FileImpl(BuildInstance instance, string name, string destination, string overrideSource = "")
        {
            var buildDirs = instance.GetStage<Stages.PrepareEngineDirectoriesStage>()!;
            var buildDatabases = instance.GetStage<Stages.PrepareEngineDatabasesStage>()!;

            // Determine the final destination
            string finalDestination;
            finalDestination = Path.IsPathFullyQualified(destination) ? destination : Path.Combine(buildDirs.BuildDir, destination);
            finalDestination = Path.Combine(finalDestination, name);

            // Use dependency system to track the copy operation
            await buildDatabases.SDK.RunIfOutdated("Install.File.Copy", name, "Install.Files", async (DependencyRecord depend) =>
            {
                // Download the file
                var downloadedFile = await Download.DownloadFile(instance, name, overrideSource);

                lock (InstallLocks.GetOrAdd($"Install.File | {Path.GetFullPath(finalDestination)} | Copy", _ => new System.Threading.Lock()))
                {
                    // Ensure destination directory exists
                    var destDir = Path.GetDirectoryName(finalDestination);
                    if (!string.IsNullOrEmpty(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    // Copy the file
                    // CopyFile will fail with System.IO.IOException: Permission denied on POSIX if destination file is read-only, so we delete that file first.
                    RemovePathIfExists(finalDestination);
                    System.IO.File.Copy(downloadedFile, finalDestination, overwrite: true);

                    // Set executable permissions on non-Windows platforms
                    if (!OperatingSystem.IsWindows())
                    {
                        System.IO.File.SetUnixFileMode(finalDestination, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                }

                depend.ExternalFiles.Add(downloadedFile);
                depend.ExternalFiles.Add(finalDestination);
            }, null, new[] { finalDestination });

            return finalDestination;
        }

        private static ConcurrentDictionary<string, System.Threading.Lock> InstallLocks = new();

        private static string GetPlatPostfix()
        {
            string plat = "";
            switch (BS.HostOS)
            {
                case OSPlatform.Windows:
                    plat = "windows";
                    break;
                case OSPlatform.Linux:
                    plat = "linux";
                    break;
                case OSPlatform.OSX:
                    plat = "macosx";
                    break;
            }
            string arch = "";
            switch (BS.HostArch)
            {
                case Architecture.X64:
                    arch = BS.HostOS == OSPlatform.OSX ? "x86_64" : "x64";
                    break;
                case Architecture.X86:
                    arch = "x86";
                    break;
                case Architecture.ARM64:
                    arch = "arm64";
                    break;
            }
            return $"-{plat}-{arch}.zip";
        }

        private static List<string> DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
        {
            List<string> copiedFiles = new List<string>();

            // Get the subdirectories for the specified directory.
            DirectoryInfo dir = new DirectoryInfo(sourceDirName);

            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException(
                    "Source directory does not exist or could not be found: "
                    + sourceDirName);
            }

            // If the destination directory doesn't exist, create it.
            if (!Directory.Exists(destDirName))
            {
                Directory.CreateDirectory(destDirName);
            }

            // Parse files.
            foreach (var attr in dir.EnumerateFileSystemInfos())
            {
                var dst = Path.Combine(destDirName, attr.Name);
                // Copy symlink in POSIX.
                if (!OperatingSystem.IsWindows() && attr.LinkTarget is not null)
                {
                    RemovePathIfExists(dst);
                    System.IO.File.CreateSymbolicLink(dst, attr.LinkTarget);
                    copiedFiles.Add(dst);
                    continue;
                }

                if (attr is FileInfo file)
                {
                    RemovePathIfExists(dst);
                    file.CopyTo(dst, overwrite: false);
                    copiedFiles.Add(dst);
                    continue;
                }
                else if (attr is DirectoryInfo subdir && copySubDirs)
                {
                    copiedFiles.AddRange(DirectoryCopy(subdir.FullName, dst, copySubDirs));
                }
            }

            return copiedFiles;
        }
    }

    public static partial class Engine
    {
        public static Task<string> InstallTool(this BuildInstance instance, string name, string destination = "", string overrideSource = "", bool platPostfix = true) =>
            Install.Tool(instance, name, destination, overrideSource, platPostfix);

        public static Task InstallSDK(this BuildInstance instance, string name, Dictionary<string, string>? directoryMappings = null, bool platPostfix = true, string overrideSource = "", bool copyToBinaryDirectory = true) =>
            Install.SDK(instance, name, directoryMappings, platPostfix, overrideSource, copyToBinaryDirectory);

        public static Task InstallSDKFromUrl(this BuildInstance instance, string name, string url, string fileName = "", string sha256 = "", Dictionary<string, string>? directoryMappings = null, bool copyToBinaryDirectory = true, bool force = false) =>
            Install.SDKFromUrl(instance, name, url, fileName, sha256, directoryMappings, copyToBinaryDirectory, force);

        public static Task<string> InstallFile(this BuildInstance instance, string name, string destination, string overrideSource = "") =>
            Install.File(instance, name, destination, overrideSource);
    }
}
