using System.IO;

namespace SB.Core
{
    using ArgumentName = string;
    using VS = VisualStudio;
    using BS = BuildInstance;
    [ArgumentDriver(InjectType = typeof(CppTarget))]
    public class LINKArgumentDriver : IArgumentDriver
    {
        public LINKArgumentDriver(bool ArchiverMode) => this.ArchiverMode = ArchiverMode;

        [TargetProperty] 
        public string RuntimeLibrary(string what) => VS.IsValidRT(what) ? what.StartsWith("MT") ? "/NODEFAULTLIB:msvcrt.lib" : "" : throw new ArgumentException($"Invalid argument \"{what}\" for MSVC RuntimeLibrary!");

        [TargetProperty] 
        public string TargetType(TargetType type) => typeMap.TryGetValue(type, out var t) ? t : throw new ArgumentException($"Invalid target type \"{type}\" for MSVC Linker!");
        static readonly Dictionary<TargetType, string> typeMap = new Dictionary<TargetType, string> { { Core.TargetType.Static, "/LIB" }, { Core.TargetType.Dynamic, "/DLL" }, { Core.TargetType.Executable, "" }, { Core.TargetType.HeaderOnly, "" } };

        [TargetProperty(InheritBehavior = true, PathBehavior = true)] 
        // LINK.exe 仍接收一整段字符串命令行，包括 response file 中继续解析出的 /LIBPATH:。
        // 这里保持原有绝对路径语义，只引用路径片段，避免中文空格工程目录在命令行拆分阶段被截断。
        public string[]? LinkDirs(ArgumentList<string> dirs) => dirs.All(x => BS.CheckPath(x, false) ? true : throw new ArgumentException($"Invalid link dir {x}!")) ? dirs.Select(dir => $"/LIBPATH:{BS.QuoteCommandLinePath(dir)}").ToArray() : null;
        
        [TargetProperty(InheritBehavior = true)] 
        // Link() 保持父提交的历史约定：只有 .o 被视为已经成形的 object 输入，其它值都表示库名并追加 .lib。
        // 中文空格路径的修复只发生在旧语义生成出最终 link item 之后，通过引用完整片段避免命令行拆分。
        public string[]? Link(ArgumentList<string> dirs) => dirs.Select(LinkArgument).ToArray();

        [TargetProperty(InheritBehavior = true)]
        public string[]? WholeArchive(ArgumentList<string> libs) => libs.Select(lib => $"/WHOLEARCHIVE:\"{lib}.lib\"").ToArray();

        [TargetProperty(InheritBehavior = true)]
        public string[]? MSVC_NoDefaultLibrary(ArgumentList<string> libs) => libs.Select(lib => lib.Length > 0 ? $"/NODEFAULTLIB:{lib}.lib" : "").ToArray();

        [TargetProperty(InheritBehavior = true)]
        public string[]? MSVC_LinkerArgs(ArgumentList<string> libs) => libs.ToArray();

        [TargetProperty(InheritBehavior = true, PathBehavior = true)]
        public string[]? ManifestInput(ArgumentList<string> manifests) => manifests.Count > 0 
            ? manifests.Select(m => BS.CheckFile(m, true) ? $"/MANIFESTINPUT:{BS.QuoteCommandLinePath(m)}" : throw new ArgumentException($"Manifest file {m} does not exist!"))
                       .Prepend("/MANIFEST:EMBED")
                       .ToArray() 
            : null;

        public string Arch(Architecture arch) => archMap.TryGetValue(arch, out var r) ? r : throw new ArgumentException($"Invalid architecture \"{arch}\" for LINK.exe!");
        static readonly Dictionary<Architecture, string> archMap = new Dictionary<Architecture, string> { { Architecture.X86, "/MACHINE:X86" }, { Architecture.X64, "/MACHINE:X64" }, { Architecture.ARM64, "/MACHINE:ARM64" } };

        [TargetProperty]
        public string DebugSymbols(bool Enable) => Enable && !ArchiverMode ? "/DEBUG:FULL" : "";

        [TargetProperty]
        public virtual string DynamicDebug(bool v) => v ? "/DEBUG:FULL /dynamicdeopt" : "";

        public string PDB(string path) => BS.CheckPath(path, false) ? $"/PDB:{BS.QuoteCommandLinePath(path)}" : throw new ArgumentException($"PDB value {path} is not a valid absolute path!");

        public string[] Inputs(ArgumentList<string> inputs) => inputs.Select(BS.QuoteCommandLinePath).ToArray();

        public string Output(string output) => BS.CheckFile(output, false) ? $"/OUT:{BS.QuoteCommandLinePath(output)}" : throw new ArgumentException($"Invalid output file path {output}!");

        private readonly bool ArchiverMode;
        public ArgumentDictionary Arguments { get; } = new ArgumentDictionary();
        public HashSet<string> RawArguments { get; } = new HashSet<string> { "/NOLOGO" };

        private string LinkArgument(string link)
        {
            var Extension = Path.GetExtension(link);
            var LinkFile = Extension != ".o" ? $"{link}.lib" : link;
            var PathLike = Path.IsPathFullyQualified(LinkFile) || LinkFile.Contains('\\') || LinkFile.Contains('/') || LinkFile.Any(char.IsWhiteSpace);
            return PathLike ? BS.QuoteCommandLinePath(LinkFile) : LinkFile;
        }
    }
}
