using Serilog;

namespace SB.Core
{
    using BS = BuildInstance;

    public class EmarArchiver : IArchiver
    {
        public EmarArchiver(Emscripten toolchain)
        {
            this.Toolchain = toolchain;
            this.ExecutablePath = toolchain.EmarPath!;
        }

        public ArchiveResult Archive(TaskEmitter Emitter, Target Target, IArgumentDriver Driver)
        {
            var BuildDatabase = Target.Instance.GetStage<Stages.PrepareBuildDatabasesStage>()!;
            var ArgsDict = Driver.CalculateArguments();
            var DependArgsList = ArgsDict.Values.SelectMany(x => x).ToList();
            DependArgsList.Add($"LINKER:ID=EMAR");
            DependArgsList.Add($"LINKER:VERSION={Version}");
            DependArgsList.Add("ARCHIVER:RECREATE=1");

            var InputFiles = Driver.Arguments["Inputs"] as ArgumentList<string>;
            var OutputFile = Driver.Arguments["Output"] as string;
            bool Changed = BuildDatabase.GetCompileDatabaseForTarget(Target).RunIfOutdated(Target.Name, OutputFile!, Emitter.Name, (DependencyRecord depend) =>
            {
                // emar mirrors ar: Output must come first on the command line.
                var OutputArg = ArgsDict["Output"];
                ArgsDict.Remove("Output");
                var ArgsList = ArgsDict.Values.SelectMany(x => x).ToList();
                var ArgsString = OutputArg[0] + " " + String.Join(" ", ArgsList);

                if (File.Exists(OutputFile))
                    File.Delete(OutputFile);

                int ExitCode = BS.RunProcess(ExecutablePath, ArgsString, out var OutputInfo, out var ErrorInfo);
                if (ExitCode != 0)
                    throw new TaskFatalError($"Archive {OutputFile} failed with fatal error!", $"emar: {ErrorInfo}");
                else if (!string.IsNullOrEmpty(OutputInfo) && OutputInfo.Contains("warning"))
                    Log.Warning("emar: {OutputInfo}", OutputInfo);

                depend.ExternalFiles.AddRange(OutputFile!);
            }, new List<string>(InputFiles!), DependArgsList);

            return new ArchiveResult
            {
                TargetFile = OutputFile!,
                IsRestored = !Changed
            };
        }

        public IArgumentDriver CreateArgumentDriver(BuildInstance Instance) => new EmarArgumentDriver();
        public Version Version => Toolchain.EmccVersion!;
        public string ExecutablePath { get; }
        private Emscripten Toolchain { get; }
    }
}
