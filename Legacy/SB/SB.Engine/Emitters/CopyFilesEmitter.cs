using SB.Core;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.FileSystemGlobbing;
using Serilog;

namespace SB
{
    using BS = BuildInstance;
    
    /// <summary>
    /// TaskEmitter for copying files from source directories to build output directories
    /// </summary>
    public class CopyFilesEmitter : TaskEmitter
    {
        public override bool EnableEmitter(BuildInstance Instance, Target Target)
        {
            var copyFileList = Target.FileList<CopyFileList>();
            return copyFileList.Specs.Count > 0 || Target.HasFilesOf<CopyFileList>();
        }
        
        public override bool EmitTargetTask(BuildInstance Instance, Target Target)
        {
            var BuildDirs = Target.Instance.GetStage<Stages.PrepareEngineDirectoriesStage>()!;
            var BuildDatabase = Target.Instance.GetStage<Stages.PrepareEngineDatabasesStage>()!;

            var CopyFileList = Target.FileList<CopyFileList>();
            var targetBuildPath = BuildDirs.BuildDir;

            var copySpecs = CopyFileList.Specs.Count > 0 ?
                CopyFileList.Specs :
                new List<CopyFileSpec>
                {
                    new()
                    {
                        RootDir = CopyFileList.RootDir,
                        SourceBaseDirectory = CopyFileList.SourceBaseDirectory,
                        Destination = CopyFileList.Destination,
                        Files = CopyFileList.Files.ToList()
                    }
                };

            foreach (var copySpec in copySpecs)
            {
                var destinationBase = Path.Combine(targetBuildPath, copySpec.Destination ?? "");
                Directory.CreateDirectory(destinationBase);

                var rootDir = copySpec.RootDir ?? copySpec.SourceBaseDirectory ?? Target.Directory;
                foreach (var sourceFile in ResolveCopyFiles(Target.Directory, copySpec.Files))
                {
                    var relativePath = Path.GetRelativePath(rootDir, sourceFile);
                    var destinationFile = Path.Combine(destinationBase, relativePath);
                    var destinationDir = Path.GetDirectoryName(destinationFile);
                    if (destinationDir != null)
                    {
                        Directory.CreateDirectory(destinationDir);
                    }

                    BuildDatabase.Misc.RunIfOutdated(Target.Name, destinationFile, Name, (depend) =>
                    {
                        Log.Verbose("Copying file {SourceFile} to {DestinationFile}", sourceFile, destinationFile);
                        File.Copy(sourceFile, destinationFile, overwrite: true);
                        depend.ExternalFiles.Add(destinationFile);
                    }, new string[] { sourceFile }, new[] { destinationFile, rootDir, destinationBase });
                }
            }
            return true;
        }

        private static IEnumerable<string> ResolveCopyFiles(string targetDirectory, IReadOnlyList<string> files)
        {
            var results = new SortedSet<string>();
            foreach (var file in files)
            {
                if (file.Contains("*"))
                {
                    var glob = Path.IsPathFullyQualified(file) ?
                        Path.GetRelativePath(targetDirectory, file) :
                        file;
                    var matcher = new Matcher();
                    matcher.AddInclude(glob);
                    foreach (var result in matcher.GetResultsInFullPath(targetDirectory))
                    {
                        results.Add(result);
                    }
                }
                else
                {
                    results.Add(Path.IsPathFullyQualified(file) ? file : Path.Combine(targetDirectory, file));
                }
            }
            return results;
        }
    }

    public class CopyFileSpec
    {
        public string? RootDir { get; set; }
        public string? SourceBaseDirectory { get; set; }
        public string? Destination { get; set; }
        public List<string> Files { get; set; } = new();
    }
    
    /// <summary>
    /// FileList for files that need to be copied to the build output
    /// </summary>
    public class CopyFileList : FileList
    {
        public List<CopyFileSpec> Specs { get; } = new();

        /// <summary>
        /// Optional: Root directory for calculating relative paths. 
        /// This is used to determine the relative path structure that will be preserved in the destination.
        /// If not set, uses SourceBaseDirectory or Target.Directory
        /// </summary>
        public string? RootDir { get; set; }
        
        /// <summary>
        /// Optional: Base directory for calculating relative paths. 
        /// If not set, uses Target.Directory
        /// </summary>
        public string? SourceBaseDirectory { get; set; }
        
        /// <summary>
        /// Optional: Destination folder relative to build directory.
        /// If not set, copies to build directory root
        /// </summary>
        public string? Destination { get; set; }
    }
    
    /// <summary>
    /// Extension methods for easily adding copy tasks to targets
    /// </summary>
    public static partial class TargetExtensions
    {
        /// <summary>
        /// Add files to be copied to the build output directory
        /// </summary>
        /// <param name="this">Target instance</param>
        /// <param name="files">Files to copy (supports glob patterns)</param>
        /// <returns>Target instance for chaining</returns>
        public static Target CopyFiles(this Target @this, params string[] files)
        {
            var fileList = @this.FileList<CopyFileList>();
            fileList.Specs.Add(new CopyFileSpec { Files = files.ToList() });
            fileList.AddFiles(files);
            return @this;
        }
        
        /// <summary>
        /// Add files to be copied with custom destination
        /// </summary>
        /// <param name="this">Target instance</param>
        /// <param name="Destination">Destination folder relative to build directory</param>
        /// <param name="files">Files to copy (supports glob patterns)</param>
        /// <returns>Target instance for chaining</returns>
        public static Target CopyFilesTo(this Target @this, string Destination, params string[] files)
        {
            var fileList = @this.FileList<CopyFileList>();
            fileList.Destination = Destination;
            fileList.Specs.Add(new CopyFileSpec
            {
                Destination = Destination,
                Files = files.ToList()
            });
            fileList.AddFiles(files);
            return @this;
        }
        
        /// <summary>
        /// Add files to be copied with custom root directory for relative path calculation
        /// </summary>
        /// <param name="this">Target instance</param>
        /// <param name="RootDir">Root directory for calculating relative paths that will be preserved in destination</param>
        /// <param name="Destination">Destination folder relative to build directory</param>
        /// <param name="files">Files to copy (supports glob patterns)</param>
        /// <returns>Target instance for chaining</returns>
        public static Target CopyFilesWithRoot(this Target @this, string RootDir, string Destination, params string[] files)
        {
            var fileList = @this.FileList<CopyFileList>();
            var resolvedRootDir = Path.IsPathFullyQualified(RootDir) 
                ? RootDir
                : Path.Combine(@this.Directory, RootDir);
            fileList.RootDir = resolvedRootDir;
            fileList.Destination = Destination;
            fileList.Specs.Add(new CopyFileSpec
            {
                RootDir = resolvedRootDir,
                Destination = Destination,
                Files = files.ToList()
            });
            fileList.AddFiles(files);
            return @this;
        }
    }
}
