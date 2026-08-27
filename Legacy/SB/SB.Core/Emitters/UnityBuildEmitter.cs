using SB.Core;

namespace SB
{
    using BS = BuildInstance;

    public class UnityBuildAttribute
    {
        public int BatchCount { get; set; } = 16;
        public required bool Cpp;
        public required bool C;
    }

    public class UnityBuildEmitter : TaskEmitter
    {
        public override bool EnableEmitter(BuildInstance instance, Target target) => (target.HasFilesOf<CppFileList>() || target.HasFilesOf<CFileList>() || target.HasFilesOf<ObjCppFileList>() || target.HasFilesOf<ObjCFileList>()) && target.HasAttribute<UnityBuildAttribute>();
        public override bool EmitTargetTask(BuildInstance instance, Target target) => true;
        public override IArtifact? PerTargetTask(BuildInstance instance, Target target)
        {
            var unityFileDirectory = Path.Combine(target.GetBuildGenDir(), "unity_build");
            Directory.CreateDirectory(unityFileDirectory);

            var buildDatabase = target.Instance.GetStage<Stages.PrepareBuildDatabasesStage>()!;
            var unityBuildAttribute = target.GetAttribute<UnityBuildAttribute>()!;
            var unityFileDirectoryFull = Path.GetFullPath(unityFileDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            bool changed = false;

            bool IsGeneratedUnityFile(string file)
            {
                var fullPath = Path.GetFullPath(file);
                return fullPath.StartsWith(unityFileDirectoryFull, StringComparison.OrdinalIgnoreCase);
            }

            var runBatch = (FileList fileList, string[] batch, string batchName, string postfix) =>
            {
                var unityFile = Path.Combine(unityFileDirectory, $"unity_build.{batchName}.{postfix}");
                changed |= buildDatabase.GetCompileDatabaseForTarget(target).RunIfOutdated(target.Name, unityFile, this.Name, (DependencyRecord depend) =>
                {
                    var unityContent = String.Join("\n", batch.Select(file => $"#include \"{file}\""));
                    File.WriteAllText(unityFile, unityContent);
                    depend.ExternalFiles.Add(unityFile);
                }, batch, null);
                fileList.RemoveFiles(batch);
                return unityFile;
            };

            var runBatches = (FileList fileList, IEnumerable<string[]> batches, string postfix) =>
            {
                List<string> unityFiles = new(batches.Count());
                int batchIndex = 0;
                foreach (var batch in batches.ToArray())
                {
                    var unityFile = runBatch(fileList, batch, batchIndex.ToString(), postfix);
                    unityFiles.Add(unityFile);
                    batchIndex += 1;
                }
                return unityFiles;
            };

            var runUnity = (FileList fileList, string language) =>
            {
                var generatedUnityFiles = fileList.Files.Where(IsGeneratedUnityFile).ToArray();
                if (generatedUnityFiles.Length > 0)
                {
                    fileList.RemoveFiles(generatedUnityFiles);
                }

                var fileGroups = fileList.Files.GroupBy(file => UnityGroup(file, fileList));
                foreach (var fileGroup in fileGroups)
                {
                    if (fileGroup.Key is null)
                        continue; // Skip files with UnityGroup == null, which means unity build is disabled
                    else if (fileGroup.Key == "")
                    {
                        var commonBatches = fileGroup.Chunk(unityBuildAttribute.BatchCount);
                        var unityFiles = runBatches(fileList, commonBatches, language);
                        fileList.AddFiles(unityFiles.ToArray());
                    }
                    else
                    {
                        var unityFile = runBatch(fileList, fileGroup.ToArray(), fileGroup.Key, language);
                        fileList.AddFiles(unityFile);
                    }
                }
            };

            if (unityBuildAttribute.C && target.HasFilesOf<CFileList>())
            {
                runUnity(target.FileList<CFileList>(), "c");
            }
            
            if (unityBuildAttribute.Cpp && target.HasFilesOf<CppFileList>())
            {
                runUnity(target.FileList<CppFileList>(), "cpp");
            }
            
            return new PlainArtifact { IsRestored = !changed };
        }

        private static string? UnityGroup(string file, FileList fileList)
        {
            var unityOptions = fileList.GetFileOptions(file) as UnityBuildableFileOptions;
            if (unityOptions is null)
                return "";
            if (unityOptions.DisableUnityBuild)
                return null;
            return unityOptions.UnityGroup;
        }
    }

    public static partial class TargetExtensions
    {
        public static CppTarget EnableUnityBuild(this CppTarget @this, int batchCount = 16, bool cpp = true, bool c = true)
        {
            @this.SetAttribute(new UnityBuildAttribute { BatchCount = batchCount, Cpp = cpp, C = c });
            return @this;
        }
    }
}
