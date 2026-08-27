using SB.Core;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SB
{
    using BS = BuildInstance;

    /// <summary>
    /// Attribute to mark targets for artifact installation
    /// </summary>
    public class InstallArtifactAttribute
    {
        /// <summary>
        /// Destination directory for installation (absolute path or relative to project root)
        /// </summary>
        public string? InstallDirectory { get; set; }
        
        /// <summary>
        /// Whether to install PDB files alongside executables/DLLs
        /// </summary>
        public bool InstallPDB { get; set; } = true;
        
        /// <summary>
        /// Whether to install the artifact
        /// </summary>
        public bool Enable { get; set; } = true;
    }

    /// <summary>
    /// TaskEmitter for installing build artifacts (EXE, DLL, PDB) to specified directories
    /// </summary>
    public class InstallArtifactEmitter : TaskEmitter
    {
        public override bool EnableEmitter(BuildInstance Instance, Target Target) 
        {
            var attr = Target.GetAttribute<InstallArtifactAttribute>();
            return attr != null && attr.Enable;
        }
        
        public override bool EmitTargetTask(BuildInstance Instance, Target Target) => true;
        
        public override IArtifact? PerTargetTask(BuildInstance Instance, Target Target)
        {
            var BuildDirs = Target.Instance.GetStage<Stages.PrepareEngineDirectoriesStage>()!;
            var BuildDatabase = Target.Instance.GetStage<Stages.PrepareBuildDatabasesStage>()!;
            var attr = Target.GetAttribute<InstallArtifactAttribute>();
            if (attr == null || !attr.Enable)
                return null;
            
            var LinkResults = Target.Instance.Artifacts.OfType<LinkResult>()
                .Where(a => a.Target == Target)
                .ToList();

            // Determine installation directory
            string installDir;
            if (!string.IsNullOrEmpty(attr.InstallDirectory))
            {
                installDir = Path.IsPathFullyQualified(attr.InstallDirectory)
                    ? attr.InstallDirectory
                    : Path.Combine(BuildDirs.TempDir, attr.InstallDirectory);
            }
            else
            {
                var Configures = Target.Instance.GetStage<Stages.LoadConfigures>()!;
                // Default installation directory based on target tags.
                if (CommandBase.TargetHasTag(Target, TargetTags.Tool))
                    installDir = Path.Combine(BuildDirs.TempDir, "tools");
                else
                    installDir = Path.Combine(BuildDirs.BuildDir, $"{Target.Instance.TargetOS}-{Target.Instance.TargetArch}-{Configures.ConfigurationName}");
            }
            
            // Ensure installation directory exists
            Directory.CreateDirectory(installDir);

            SortedDictionary<string, string> FilesToCopy = new();
            if (LinkResults.Any())
            {
                var LinkResult = LinkResults.First();
                AddFileToCopy(
                    FilesToCopy,
                    LinkResult.TargetFile,
                    Path.Combine(installDir, Path.GetFileName(LinkResult.TargetFile)));
                if (attr.InstallPDB && !string.IsNullOrEmpty(LinkResult.PDBFile))
                {
                    AddFileToCopy(
                        FilesToCopy,
                        LinkResult.PDBFile,
                        Path.Combine(installDir, Path.GetFileName(LinkResult.PDBFile)));
                }
            }

            var runtimeDependencyNames = CollectRuntimeDependencyNames(Target);
            foreach (var DependencyLinkResult in Target.Instance.Artifacts.OfType<LinkResult>()
                .Where(a => runtimeDependencyNames.Contains(a.Target.Name)))
            {
                if (IsDynamicLibrary(DependencyLinkResult.TargetFile))
                {
                    AddFileToCopy(
                        FilesToCopy,
                        DependencyLinkResult.TargetFile,
                        Path.Combine(
                            installDir,
                            Path.GetFileName(DependencyLinkResult.TargetFile)));
                }
                if (attr.InstallPDB && !string.IsNullOrEmpty(DependencyLinkResult.PDBFile))
                {
                    AddFileToCopy(
                        FilesToCopy,
                        DependencyLinkResult.PDBFile,
                        Path.Combine(
                            installDir,
                            Path.GetFileName(DependencyLinkResult.PDBFile)));
                }
            }
            if (FilesToCopy.Count == 0)
            {
                Log.Verbose(
                    "No artifacts found for target {TargetName}, skipping installation",
                    Target.Name);
                return null;
            }

            bool Changed = BuildDatabase.GetCompileDatabaseForTarget(Target).RunIfOutdated(Target.Name, "InstallArtifact", this.Name,
                (DependencyRecord depend) =>
                {
                    foreach (var FilePair in FilesToCopy)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(FilePair.Value)!);
                        File.Copy(FilePair.Key, FilePair.Value, overwrite: true);
                    }
                    depend.ExternalFiles.AddRange(FilesToCopy.Values);
                }, FilesToCopy.Keys, null);
            
            return new InstallResult
            {
                Target = Target,
                InstallDirectory = installDir,
                IsRestored = !Changed
            };
        }

        private static bool IsDynamicLibrary(string file)
        {
            if (!File.Exists(file))
                return false;

            var extension = Path.GetExtension(file);
            return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".so", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddFileToCopy(
            SortedDictionary<string, string> filesToCopy,
            string sourceFile,
            string destinationFile)
        {
            if (!File.Exists(sourceFile))
                return;

            if (Path.GetFullPath(sourceFile).Equals(Path.GetFullPath(destinationFile), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            filesToCopy[sourceFile] = destinationFile;
        }

        private static HashSet<string> CollectRuntimeDependencyNames(Target target)
        {
            HashSet<string> dependencyNames = new();
            Queue<string> pending = new();

            foreach (var dependencyName in target.Dependencies)
            {
                pending.Enqueue(dependencyName);
            }

            while (pending.Count > 0)
            {
                var dependencyName = pending.Dequeue();
                if (!dependencyNames.Add(dependencyName))
                {
                    continue;
                }

                var dependencyTarget = target.Instance.GetTarget(dependencyName);
                if (dependencyTarget == null)
                {
                    continue;
                }

                foreach (var nextDependency in dependencyTarget.PublicDependencies)
                {
                    pending.Enqueue(nextDependency);
                }
                foreach (var nextDependency in dependencyTarget.PrivateDependencies)
                {
                    pending.Enqueue(nextDependency);
                }
                foreach (var nextDependency in dependencyTarget.InterfaceDependencies)
                {
                    pending.Enqueue(nextDependency);
                }
            }

            return dependencyNames;
        }
    }
    
    /// <summary>
    /// Result of artifact installation
    /// </summary>
    public struct InstallResult : IArtifact
    {
        public required Target Target { get; init; }
        public required string InstallDirectory { get; init; }
        public bool IsRestored { get; init; }
    }
    
    /// <summary>
    /// Extension methods for configuring artifact installation
    /// </summary>
    public static partial class TargetExtensions
    {
        /// <summary>
        /// Configure the target to install its artifacts to a specific directory
        /// </summary>
        public static Target InstallArtifactTo(this Target @this, string installDirectory, bool installPDB = true)
        {
            @this.SetAttribute(new InstallArtifactAttribute
            {
                InstallDirectory = installDirectory,
                InstallPDB = installPDB,
                Enable = true
            });
            return @this;
        }
        
        /// <summary>
        /// Configure the target to install its artifacts using the default directory
        /// </summary>
        public static Target InstallArtifact(this Target @this, bool installPDB = true)
        {
            @this.SetAttribute(new InstallArtifactAttribute
            {
                InstallPDB = installPDB,
                Enable = true
            });
            return @this;
        }
        
        /// <summary>
        /// Disable artifact installation for this target
        /// </summary>
        public static Target NoInstallArtifact(this Target @this)
        {
            @this.SetAttribute(new InstallArtifactAttribute
            {
                Enable = false
            });
            return @this;
        }
    }
}
