using SB.Core;
using Serilog;
using System.Diagnostics;

namespace SB
{
    using BS = BuildInstance;
    public class CppLinkAttribute
    {
        public List<string> BypassLinks { get; init; } = new();
        public List<string> BypassLinkDirs { get; init; } = new();
        public List<string> BypassAppleFrameworks { get; init; } = new();
    }

    public class CppLinkEmitter : TaskEmitter
    {
        public CppLinkEmitter(IToolchain Toolchain) => this.Toolchain = Toolchain;
        public override bool EnableEmitter(BuildInstance Instance, Target Target) => Target.HasFilesOf<CppFileList>() || Target.HasFilesOf<CFileList>() || Target.HasFilesOf<ObjCppFileList>() || Target.HasFilesOf<ObjCFileList>() || Target.HasFilesOf<RcFileList>();
        public override bool EmitTargetTask(BuildInstance Instance, Target Target) => true;
        public override IArtifact? PerTargetTask(BuildInstance Instance, Target Target)
        {
            var CppLinkAttr = Target.GetAttribute<CppLinkAttribute>()!;
            var TT = Target.GetTargetType();
            Stopwatch sw = new();
            sw.Start();

            var LinkedFileName = GetLinkedFileName(Target);
            var DependFile = Path.Combine(Target.GetBuildSrcDepsDir(), BS.GetUniqueTempFileName(LinkedFileName, Target.Name + this.Name, "task.deps.json"));
            var Inputs = new ArgumentList<string>();

            // Add obj files
            var Objects = Target.GetAttribute<CppCompileAttribute>()!.ObjectFiles;
            Inputs.AddRange(Objects);
            
            // Add res files from RC compilation
            if (Target.HasFilesOf<RcFileList>())
            {
                var RcCompileAttr = Target.GetAttribute<RcCompileAttribute>();
                if (RcCompileAttr != null)
                {
                    Inputs.AddRange(RcCompileAttr.ResourceFiles);
                }
            }

            if (TT == TargetType.Dynamic || TT == TargetType.Executable)
            {
                var TargetArguments = Target.Arguments.ToDictionary();
                
                // Add manifest files to linker arguments (Windows only)
                if (Target.Instance.TargetOS == OSPlatform.Windows && Target.HasFilesOf<ManifestFileList>())
                {
                    var ManifestFiles = Target.FileLists.OfType<ManifestFileList>().SelectMany(fl => fl.Files).ToList();
                    if (ManifestFiles.Count > 0)
                    {
                        var ManifestInput = TargetArguments.GetOrAddNew<string, ArgumentList<string>>("ManifestInput");
                        ManifestInput.AddRange(ManifestFiles);
                    }
                }

                // Add dep obj files
                Inputs.AddRange(Target.IgnoreVisibilityAllDependencies.Where(
                    Dep =>
                    {
                        var depTarget = Target.Instance.GetTarget(Dep);
                        return depTarget is not null &&
                            !depTarget.IsBinaryDependencyTarget() &&
                            depTarget.GetTargetType() == TargetType.Objects;
                    }
                ).SelectMany(
                    Dep => Target.Instance.GetTarget(Dep)!.GetAttribute<CppCompileAttribute>()!.ObjectFiles
                ));
                // Add lib file of dep target
                Inputs.AddRange(
                    Target.IgnoreVisibilityAllDependencies
                        .Select(Dependency => Target.Instance.GetTarget(Dependency)!)
                        .Where(Dependency => !Dependency.IsBinaryDependencyTarget())
                        .Select(GetStubFileName)
                        .Where(Stub => Stub != null)
                        .Select(Stub => Stub!)
                );
                // Collect private linker inputs from static and object targets
                var Links = TargetArguments.GetOrAddNew<string, ArgumentList<string>>("Link");
                var LinkDirs = TargetArguments.GetOrAddNew<string, ArgumentList<string>>("LinkDirs");
                Links.AddRange(
                    Target.IgnoreVisibilityAllDependencies
                        .Select(Dependency => Target.Instance.GetTarget(Dependency)!)
                        .Where(Dependency => !Dependency.IsBinaryDependencyTarget())
                        .Select(Dependency => Dependency.GetAttribute<CppLinkAttribute>()!)
                        .SelectMany(A => A.BypassLinks)
                );
                LinkDirs.AddRange(
                    Target.IgnoreVisibilityAllDependencies
                        .Select(Dependency => Target.Instance.GetTarget(Dependency)!)
                        .Where(Dependency => !Dependency.IsBinaryDependencyTarget())
                        .Select(Dependency => Dependency.GetAttribute<CppLinkAttribute>()!)
                        .SelectMany(A => A.BypassLinkDirs)
                );
                if (Target.Instance.TargetOS == OSPlatform.OSX)
                {
                    var AppleFrameworks = TargetArguments.GetOrAddNew<string, ArgumentList<string>>("AppleFramework");
                    AppleFrameworks.AddRange(
                        Target.IgnoreVisibilityAllDependencies
                            .Select(Dependency => Target.Instance.GetTarget(Dependency)!)
                            .Where(Dependency => !Dependency.IsBinaryDependencyTarget())
                            .Select(Dependency => Dependency.GetAttribute<CppLinkAttribute>()!)
                            .SelectMany(A => A.BypassAppleFrameworks)
                    );

                    var linkerArgs = TargetArguments.GetOrAddNew<string, ArgumentList<string>>("AppleClang_LinkerArgs");
                    if (TT == TargetType.Dynamic)
                    {
                        var dylib = Path.GetFileName(LinkedFileName);
                        linkerArgs.Add($"-Wl,-install_name,@rpath/{dylib}");
                        linkerArgs.Add("-Wl,-rpath,@loader_path");
                    }
                    else if (TT == TargetType.Executable)
                    {
                        linkerArgs.Add("-Wl,-rpath,@executable_path");
                    }
                }
                // Link
                if (Inputs.Count != 0)
                {
                    var LINKDriver = Toolchain.Linker.CreateArgumentDriver(Target.Instance)
                        .AddArguments(TargetArguments)
                        .AddArgument("Inputs", Inputs)
                        .AddArgument("Output", LinkedFileName);
                    if (TargetArguments.TryGetValue("DebugSymbols", out var Enable) && (bool)Enable!)
                    {
                        if (Target.Instance.TargetOS == OSPlatform.Windows)
                        {
                            LINKDriver.AddArgument("PDB", LinkedFileName.Replace("dll", "pdb").Replace("exe", "pdb"));
                        }
                    }
                    return Toolchain.Linker.Link(this, Target, LINKDriver);
                }
            }
            else if (TT == TargetType.Static)
            {
                // Archive only
                if (Inputs.Count != 0)
                {
                    var ARDriver = Toolchain.Archiver.CreateArgumentDriver(Target.Instance)
                        .AddArguments(Target.Arguments)
                        .AddArgument("Inputs", Inputs)
                        .AddArgument("Output", LinkedFileName);
                    return Toolchain.Archiver.Archive(this, Target, ARDriver);
                }
            }
            if (TT == TargetType.Static || TT == TargetType.Objects || TT == TargetType.HeaderOnly)
            {
                // bypass 'Link' vars (include private)
                if (Target.Arguments.TryGetValue("Link", out var LinkArgs))
                {
                    var Links = LinkArgs as ArgumentList<string>;
                    CppLinkAttr.BypassLinks.AddRange(Links!.ToList());
                }
                // bypass 'LinkDir' vars
                if (Target.Arguments.TryGetValue("LinkDirs", out var LinkDirArgs))
                {
                    var LinkDirs = LinkDirArgs as ArgumentList<string>;
                    CppLinkAttr.BypassLinkDirs.AddRange(LinkDirs!.ToList());
                }
                // bypass 'AppleFramework' vars
                if (Target.Arguments.TryGetValue("AppleFramework", out var AppleFrameworkArgs))
                {
                    var AppleFrameworks = AppleFrameworkArgs as ArgumentList<string>;
                    CppLinkAttr.BypassAppleFrameworks.AddRange(AppleFrameworks!.ToList());
                }
            }
            sw.Stop();
            AddElapsedMilliseconds(sw.ElapsedMilliseconds);
            return null;
        }

        public static string GetLinkedFileName(Target Target)
        {
            var OutputType = Target.GetTargetType();
            var Extension = GetPlatformLinkedFileExtension(Target, OutputType);
            var OutputFile = Path.Combine(Target.GetBinaryDir(), $"{Target.Name}{Extension}");
            return OutputFile;
        }

        public static string? GetStubFileName(Target Target)
        {
            var OutputType = Target.GetTargetType();
            var Extension = GetPlatformStubFileExtension(Target, OutputType);
            if (Extension.Length == 0)
                return null;
            var OutputFile = Path.Combine(Target.GetBinaryDir(), $"{Target.Name}{Extension}");
            return OutputFile;
        }

        public static string GetPlatformLinkedFileExtension(Target Target, TargetType? Type)
        {
            if (Target.Instance.TargetOS == OSPlatform.Windows)
                return Type switch
                {
                    TargetType.Static => ".lib",
                    TargetType.Dynamic => ".dll",
                    TargetType.Executable => ".exe",
                    _ => ""
                };
            else if (Target.Instance.TargetOS == OSPlatform.OSX)
                return Type switch
                {
                    TargetType.Static => ".a",
                    TargetType.Dynamic => ".dylib",
                    TargetType.Executable => "",
                    _ => ""
                };
            else if (Target.Instance.TargetOS == OSPlatform.Linux)
                return Type switch
                {
                    TargetType.Static => ".a",
                    TargetType.Dynamic => ".so",
                    TargetType.Executable => "",
                    _ => ""
                };
            else if (Target.Instance.TargetOS == OSPlatform.Emscripten)
                return Type switch
                {
                    // .js is the default emcc output; em++ drops a sibling
                    // .wasm next to it. Dynamic libraries use an archive artifact.
                    TargetType.Static => ".a",
                    TargetType.Dynamic => ".a",
                    TargetType.Executable => ".js",
                    _ => ""
                };
            return "";
        }

        private static string GetPlatformStubFileExtension(Target Target, TargetType? Type)
        {
            if (Target.Instance.TargetOS == OSPlatform.Windows)
                return Type switch
                {
                    TargetType.Static => ".lib",
                    TargetType.Dynamic => ".lib",
                    _ => ""
                };
            else if (Target.Instance.TargetOS == OSPlatform.OSX)
                return Type switch
                {
                    TargetType.Static => ".a",
                    TargetType.Dynamic => ".dylib",
                    _ => ""
                };
            else if (Target.Instance.TargetOS == OSPlatform.Linux)
                return Type switch
                {
                    TargetType.Static => ".a",
                    TargetType.Dynamic => ".a",
                    _ => ""
                };
            else if (Target.Instance.TargetOS == OSPlatform.Emscripten)
                return Type switch
                {
                    TargetType.Static => ".a",
                    TargetType.Dynamic => ".a",
                    _ => ""
                };
            return "";
        }

        private IToolchain Toolchain { get; }
    }
}
