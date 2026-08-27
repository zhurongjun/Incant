using SB.Core;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SB
{
    using BS = BuildInstance;
    public class CppCompileAttribute
    {
        public ConcurrentBag<string> ObjectFiles = new();
    }
    public class CppCompileEmitter : TaskEmitter
    {
        public CppCompileEmitter(IToolchain Toolchain) => this.Toolchain = Toolchain;
        public override bool EnableEmitter(BuildInstance Instance, Target Target) => Target.HasFilesOf<CppFileList>() || Target.HasFilesOf<CFileList>() || Target.HasFilesOf<ObjCppFileList>() || Target.HasFilesOf<ObjCFileList>();
        public override bool EmitFileTask(BuildInstance Instance, Target Target, FileList FileList) => FileList.Is<CppFileList>() || FileList.Is<CFileList>() || FileList.Is<ObjCppFileList>() || FileList.Is<ObjCFileList>();
        public override IArtifact? PerFileTask(BuildInstance Instance, Target Target, FileList FileList, FileOptions? FileOptions, string SourceFile)
        {
            Stopwatch sw = new();
            sw.Start();

            CFamily Language = FileList.Is<ObjCppFileList>() ? CFamily.ObjCpp : FileList.Is<ObjCFileList>() ? CFamily.ObjC : FileList.Is<CppFileList>() ? CFamily.Cpp : CFamily.C;
            var SourceDependencies = Path.Combine(Target.GetBuildSrcDepsDir(), BS.GetUniqueTempFileName(SourceFile, this.Name, "source.deps.json"));
            var ObjectFile = GetObjectFilePath(Target, SourceFile);
            var CompilerDriver = Toolchain.Compiler.CreateArgumentDriver(Target.Instance, Language, false)
                .AddArguments(Target.Arguments)
                .MergeArguments(FileOptions?.Arguments, true)
                .AddArgument("Source", SourceFile)
                .AddArgument("Object", ObjectFile)
                .AddArgument("SourceDependencies", SourceDependencies);
            var CFamilyOptions = FileOptions as CFamilyFileOptions;
            if ((SourceFile.EndsWith(".cpp") || SourceFile.EndsWith(".cc")) && CFamilyOptions?.DisablePCH != true)
            {
                var UsePCH = Target.GetAttribute<UsePCHAttribute>();
                if (UsePCH?.CppPCHAST is not null)
                {
                    CompilerDriver.AddArgument("UsePCHAST", UsePCH.CppPCHAST);
                }
            }
            var R = Toolchain.Compiler.Compile(this, Target, CompilerDriver);
            var CompileAttribute = Target.GetAttribute<CppCompileAttribute>()!;
            CompileAttribute.ObjectFiles.Add(ObjectFile);

            sw.Stop();
            AddElapsedMilliseconds(sw.ElapsedMilliseconds);
            return R;
        }

        public static string GetObjectFilePath(Target Target, string SourceFile) => Path.Combine(Target.GetBuildObjsDir(), BS.GetUniqueTempFileName(SourceFile, Target.Name, "obj"));

        private IToolchain Toolchain { get; }
    }

    public class UnityBuildableFileOptions : FileOptions
    {
        public bool DisableUnityBuild = false;
        public string UnityGroup = "";
    }

    public class CFamilyFileOptions : UnityBuildableFileOptions
    {
        public bool DisablePCH = false;
    }
    public class CFileList : FileList {}
    public class CppFileList : FileList {}
    public class ObjCFileList : FileList {}
    public class ObjCppFileList : FileList {}
    public class RcFileList : FileList {}
    public class ManifestFileList : FileList {}

    public static partial class TargetExtensions
    {
        public static CppTarget AddCppFiles(this CppTarget @this, params string[] Files)
        {
            @this.FileList<CppFileList>().AddFiles(Files);
            return @this;
        }

        public static CppTarget AddCFiles(this CppTarget @this, params string[] Files)
        {
            @this.FileList<CFileList>().AddFiles(Files);
            return @this;
        }

        public static CppTarget AddObjCppFiles(this CppTarget @this, params string[] Files)
        {
            @this.FileList<ObjCppFileList>().AddFiles(Files);
            return @this;
        }

        public static CppTarget AddObjCFiles(this CppTarget @this, params string[] Files)
        {
            @this.FileList<ObjCFileList>().AddFiles(Files);
            return @this;
        }

        public static CppTarget AddCppFiles(this CppTarget @this, CFamilyFileOptions Options, params string[] Files)
        {
            @this.FileList<CppFileList>().AddFiles(Options, Files);
            return @this;
        }

        public static CppTarget AddCFiles(this CppTarget @this, CFamilyFileOptions Options, params string[] Files)
        {
            @this.FileList<CFileList>().AddFiles(Options, Files);
            return @this;
        }

        public static CppTarget AddObjCppFiles(this CppTarget @this, CFamilyFileOptions Options, params string[] Files)
        {
            @this.FileList<ObjCppFileList>().AddFiles(Options, Files);
            return @this;
        }

        public static CppTarget AddObjCFiles(this CppTarget @this, CFamilyFileOptions Options, params string[] Files)
        {
            @this.FileList<ObjCFileList>().AddFiles(Options, Files);
            return @this;
        }

        public static bool HasCFamilyFiles(this CppTarget @this)
        {
            return @this.HasFilesOf<CppFileList>() || @this.HasFilesOf<CFileList>() || @this.HasFilesOf<ObjCppFileList>() || @this.HasFilesOf<ObjCFileList>();
        }

        public static CppTarget AddWindowsRCFiles(this CppTarget @this, params string[] Files)
        {
            if (@this.Instance.TargetOS == OSPlatform.Windows)
            {
                @this.FileList<RcFileList>().AddFiles(Files);
            }
            return @this;
        }

        public static CppTarget EmbedManifest(this CppTarget @this, params string[] Files)
        {
            if (@this.Instance.TargetOS == OSPlatform.Windows)
            {
                @this.FileList<ManifestFileList>().AddFiles(Files);
            }
            return @this;
        }
    }
}
