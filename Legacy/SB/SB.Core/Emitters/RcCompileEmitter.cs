using SB.Core;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SB
{
    using BS = BuildInstance;
    public class RcCompileAttribute
    {
        public ConcurrentBag<string> ResourceFiles = new();
    }
    
    public class RcCompileEmitter : TaskEmitter
    {
        public RcCompileEmitter(IToolchain Toolchain) => this.Toolchain = Toolchain;
        
        public override bool EnableEmitter(BuildInstance Instance, Target Target) => Target.HasFilesOf<RcFileList>();
        public override bool EmitFileTask(BuildInstance Instance, Target Target, FileList FileList) => FileList.Is<RcFileList>();
        
        public override IArtifact? PerFileTask(BuildInstance Instance, Target Target, FileList FileList, FileOptions? FileOptions, string SourceFile)
        {
            if (Target.Instance.TargetOS != OSPlatform.Windows)
                return null;
                
            if (Toolchain is not VisualStudio vsToolchain || vsToolchain.RC is null)
                return null;

            Stopwatch sw = new();
            sw.Start();

            var ResourceFile = GetResourceFilePath(Target, SourceFile);
            // Set working directory to RC file's directory so it can find referenced files like icon.ico
            var RcFileDirectory = Path.GetDirectoryName(SourceFile)!;
            var ResourceCompilerDriver = vsToolchain.RC.CreateArgumentDriver(Target.Instance)
                .AddArguments(Target.Arguments)
                .MergeArguments(FileOptions?.Arguments, true)
                .AddArgument("Source", SourceFile)
                .AddArgument("Output", ResourceFile);
                
            var R = vsToolchain.RC.Compile(this, Target, ResourceCompilerDriver, RcFileDirectory);
            var RcCompileAttribute = Target.GetAttribute<RcCompileAttribute>()!;
            RcCompileAttribute.ResourceFiles.Add(ResourceFile);

            sw.Stop();
            AddElapsedMilliseconds(sw.ElapsedMilliseconds);
            return R;
        }

        public static string GetResourceFilePath(Target Target, string SourceFile) => Path.Combine(Target.GetBuildObjsDir(), BS.GetUniqueTempFileName(SourceFile, Target.Name, "res"));

        private IToolchain Toolchain { get; }
    }
}
