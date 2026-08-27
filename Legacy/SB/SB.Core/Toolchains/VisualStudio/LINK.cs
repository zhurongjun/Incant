using Serilog;
using System.Diagnostics;
using System.Text;

namespace SB.Core
{
    using VS = VisualStudio;
    using BS = BuildInstance;
    public class LINK : ILinker, IArchiver
    {
        public LINK(string ExePath, Dictionary<string, string?> Env)
        {
            VCEnvVariables = Env;
            MSVCVersion = Version.Parse(VCEnvVariables["VCToolsVersion"]!);
            this.ExePath = ExePath;

            if (!File.Exists(ExePath))
                throw new ArgumentException($"LINK: ExePath: {ExePath} is not an existed absolute path!");

            Log.Information("LINK.exe version ... {MSVCVersion}", MSVCVersion);
        }

        public LinkResult Link(TaskEmitter Emitter, Target Target, IArgumentDriver Driver)
        {
            var BuildDirs = Target.Instance.GetStage<Stages.PrepareBuildDirectoriesStage>()!;
            var BuildDatabase = Target.Instance.GetStage<Stages.PrepareBuildDatabasesStage>()!;
            var LinkerArgsDict = Driver.CalculateArguments();

            // FUCK YOU MICROSOFT AGAIN AND AGAIN
            // WE CAN NOT PUT /LIB, /DLL INTO ARGS.txt
            var TargetTypeArg = LinkerArgsDict["TargetType"][0];
            LinkerArgsDict.Remove("TargetType");

            var LinkerArgsList = LinkerArgsDict.Values.SelectMany(x => x).ToList();
            var DependArgsList = LinkerArgsList.ToList();
            // Version of LINE.exe may change
            DependArgsList.Add($"ENV:VCToolsVersion={VCEnvVariables["VCToolsVersion"]}");
            // LINE.exe links against Windows DLLs with syslinks so we need to add the version in deps
            DependArgsList.Add($"ENV:WindowsSDKVersion={VCEnvVariables["WindowsSDKVersion"]}");
            // LINE.exe links against system CRT libs implicitly so we need to add the version in deps
            DependArgsList.Add($"ENV:UCRTVersion={VCEnvVariables["UCRTVersion"]}");

            var InputFiles = Driver.Arguments["Inputs"] as ArgumentList<string>;
            var OutputFile = Driver.Arguments["Output"] as string;
            
            var AllDependencyFiles = new List<string>(InputFiles!);
            if (Driver.Arguments.TryGetValue("ManifestInput", out var manifestArg) && manifestArg is ArgumentList<string> manifests)
            {
                AllDependencyFiles.AddRange(manifests);
            }

            bool Changed = BuildDatabase.GetCompileDatabaseForTarget(Target).RunIfOutdated(Target.Name, OutputFile!, Emitter.Name, (DependencyRecord depend) =>
            {
                var StringLength = LinkerArgsList.Sum(x => x.Length);
                string Arguments = "";
                string ResponseFile = "";
                if (StringLength > 30000)
                {
                    var Content = String.Join("\n", LinkerArgsList);
                    ResponseFile = Path.Combine(BuildDirs.BuildDir, $"{Guid.CreateVersion7()}.txt");
                    // LINK 会自己读取 response file；如果这里使用默认 UTF-8，中文绝对路径会在 LINK
                    // 读取 rsp 内容时按本地代码页失真。UTF-16 LE 让 rsp 内部也保持绝对路径的 Unicode 语义。
                    File.WriteAllText(ResponseFile, Content, Encoding.Unicode);

                    // LINK 的 response file 前缀是 @，需要保留在引号外；路径仍保持绝对路径语义，
                    // 只在字符串命令行边界引用路径片段，避免工程目录中的空格把参数拆开。
                    Arguments = $"{TargetTypeArg} @{BS.QuoteCommandLinePath(ResponseFile)}";
                }
                else
                {
                    Arguments = $"{TargetTypeArg} {String.Join(" ", LinkerArgsList)}";
                }
                ProcessOptions Options = new ProcessOptions
                {
                    Environment = VCEnvVariables,
                    WorkingDirectory = null,
                    EnableTimeout = true,
                    TimeoutMilliseconds = 20 * 60 * 1000 // 20 minutes
                };
                int ExitCode = BuildInstance.RunProcess(ExePath, Arguments, out var OutputInfo, out var ErrorInfo, Options);
                if (ResponseFile != "")
                {
                    File.Delete(ResponseFile);
                }

                // FUCK YOU MICROSOFT THIS IS WEIRD, WHY YOU DUMP ERRORS THROUGH STDOUT ?
                if (ExitCode != 0)
                    throw new TaskFatalError($"Link {OutputFile} failed with fatal error!", $"LINK.exe: {OutputInfo}");
                else if (OutputInfo.Contains("warning LNK"))
                    Log.Warning("LINK.exe: {OutputInfo}", OutputInfo);

                depend.ExternalFiles.AddRange(OutputFile!);
            }, AllDependencyFiles, DependArgsList);

            return new LinkResult
            {
                Target = Target,
                TargetFile = (Driver.Arguments["Output"] as string)!,
                PDBFile = Driver.Arguments.TryGetValue("PDB", out var args) ? (string)args! : "",
                IsRestored = !Changed
            };
        }

        IArgumentDriver ILinker.CreateArgumentDriver(BuildInstance Instance) => new LINKArgumentDriver(false);

        public ArchiveResult Archive(TaskEmitter Emitter, Target Target, IArgumentDriver Driver)
        {
            var LR = Link(Emitter, Target, Driver);
            return new ArchiveResult
            {
                TargetFile = LR.TargetFile,
                IsRestored = LR.IsRestored
            };
        }

        IArgumentDriver IArchiver.CreateArgumentDriver(BuildInstance Instance) => new LINKArgumentDriver(true);

        public Version Version => MSVCVersion;

        public readonly Dictionary<string, string?> VCEnvVariables;
        private readonly Version MSVCVersion;
        private readonly string ExePath;
    }
}
