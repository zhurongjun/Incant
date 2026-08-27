using Serilog;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SB.Core
{
    using VS = VisualStudio;
    using BS = BuildInstance;
    
    [ArgumentDriver(InjectType = typeof(CppTarget))]
    public class RCArgumentDriver : IArgumentDriver
    {
        [TargetProperty(InheritBehavior = true, PathBehavior = true)]
        // RC.exe 参数仍由 SB 拼成一整段字符串命令行。这里保持 include 的绝对路径语义，
        // 只引用路径片段，避免工程路径中的空格把 /I 参数拆开。
        public string[]? IncludeDirs(ArgumentList<string> dirs) => dirs.All(x => BS.CheckPath(x, true) ? true : throw new ArgumentException($"Invalid include dir {x}!")) ? dirs.Select(dir => $"/I{BS.QuoteCommandLinePath(dir)}").ToArray() : null;

        [TargetProperty(InheritBehavior = true)]
        public string[]? Defines(ArgumentList<string> defines) => defines.Select(DefineArgument).ToArray();

        // Source file will be handled separately and placed at the end of command line
        public string Source(string path) => BS.CheckFile(path, true) ? BS.QuoteCommandLinePath(path) : throw new ArgumentException($"Source value {path} is not an existed absolute path!");

        public string Output(string path) => BS.CheckFile(path, false) ? $"/fo{BS.QuoteCommandLinePath(path)}" : throw new ArgumentException($"Output value {path} is not a valid absolute path!");

        // RC.exe doesn't need architecture-specific arguments, architecture is handled by linker
        public string Arch(Architecture arch) => "";

        public ArgumentDictionary Arguments { get; } = new ArgumentDictionary();
        public HashSet<string> RawArguments { get; } = new HashSet<string> { "/nologo" };

        private static string DefineArgument(string define)
        {
            var Argument = $"/d{define}";
            // Engine 级路径宏会以 \" 包住字符串字面量。RC.exe 需要收到普通引号，
            // 再由外层命令行引用保证包含空格的整个 /d 片段不会被拆开。
            return Argument.Any(char.IsWhiteSpace) ? BS.QuoteCommandLineArgument(Argument.Replace("\\\"", "\"")) : Argument;
        }
    }

    public class RCCompiler : IResourceCompiler
    {
        public RCCompiler(string ExePath, Dictionary<string, string?> Env)
        {
            VCEnvVariables = Env;
            this.ExecutablePath = ExePath;

            if (!File.Exists(ExePath))
                throw new ArgumentException($"RCCompiler: ExePath: {ExePath} is not an existed absolute path!");

            ProcessOptions Options = new ProcessOptions
            {
                Environment = VCEnvVariables,
                WorkingDirectory = null,
                EnableTimeout = true,
                TimeoutMilliseconds = 20 * 60 * 1000 // 20 minutes
            };
            // RC.exe 通常不输出版本信息，我们使用一个默认版本
            RCVersion = new Version(10, 0);
            Log.Information("RC.exe found at: {ExePath}", ExePath);
        }

        public IArgumentDriver CreateArgumentDriver(BuildInstance Instance) => new RCArgumentDriver();

        public ResourceCompileResult Compile(TaskEmitter Emitter, Target Target, IArgumentDriver Driver, string? WorkDirectory = null)
        {
            var BuildDatabase = Target.Instance.GetStage<Stages.PrepareBuildDatabasesStage>()!;
            var SourceFile = Driver.Arguments["Source"] as string;
            var OutputFile = Driver.Arguments["Output"] as string;
            
            var CompilerArgsDict = Driver.CalculateArguments();
            // Remove Source from arguments dict as it should be the last argument for RC.exe
            var SourceArg = CompilerArgsDict["Source"][0];
            CompilerArgsDict.Remove("Source");
            var CompilerArgsList = CompilerArgsDict.Values.SelectMany(x => x).Where(arg => !string.IsNullOrEmpty(arg)).ToList();
            var DependArgsList = CompilerArgsList.ToList();
            DependArgsList.Add($"COMPILER:ID={ExecutablePath}");
            DependArgsList.Add($"COMPILER:VERSION={Version}");
            DependArgsList.Add($"ENV:VCToolsVersion={VCEnvVariables["VCToolsVersion"]}");
            DependArgsList.Add($"ENV:WindowsSDKVersion={VCEnvVariables["WindowsSDKVersion"]}");

            bool Changed = BuildDatabase.GetCompileDatabaseForTarget(Target).RunIfOutdated(Target.Name, SourceFile!, Emitter.Name, (DependencyRecord depend) =>
            {
                // RC.exe 需要 input.rc 放在最后；source/output/include 保持绝对路径语义。
                // WorkDirectory 只服务于 .rc 内部的 logo.ico 等相对资源引用，不参与路径规避。
                var Args = CompilerArgsList.Count > 0 
                    ? String.Join(" ", CompilerArgsList) + " " + SourceArg
                    : SourceArg;
                ProcessOptions Options = new ProcessOptions
                {
                    Environment = VCEnvVariables,
                    WorkingDirectory = WorkDirectory,
                    EnableTimeout = true,
                    TimeoutMilliseconds = 20 * 60 * 1000 // 20 minutes
                };
                int ExitCode = BuildInstance.RunProcess(ExecutablePath, Args, out var OutputInfo, out var ErrorInfo, Options);

                var BYTES = Encoding.Default.GetBytes(OutputInfo);
                OutputInfo = Encoding.UTF8.GetString(BYTES);
                
                if (ExitCode != 0)
                {
                    throw new TaskFatalError($"Compile resource {SourceFile} failed with fatal error!", $"RC.exe: {OutputInfo}");
                }

                if (OutputInfo.Contains("warning"))
                    Log.Warning("RC.exe: {TargetName} {OutputInfo}", Target.Name, OutputInfo);

                depend.ExternalFiles.Add(OutputFile!);
            }, new List<string> { SourceFile! }, DependArgsList);

            return new ResourceCompileResult
            {
                ResourceFile = OutputFile!,
                IsRestored = !Changed
            };
        }

        public Version Version => RCVersion;
        public readonly Dictionary<string, string?> VCEnvVariables;
        private readonly Version RCVersion;
        public string ExecutablePath { get; }
    }
}
