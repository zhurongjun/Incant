using Serilog;

namespace SB.Core
{
    using BS = BuildInstance;

    /// <summary>
    /// Drives emcc / em++ for both compilation and linking. Mirrors
    /// <see cref="AppleClangCompiler"/> since emcc IS clang under the hood,
    /// just dispatched through .bat (Windows) or shell (Unix) wrappers.
    /// </summary>
    public class EmccCompiler : ICompiler, ILinker
    {
        public EmccCompiler(Emscripten toolchain)
        {
            this.Toolchain = toolchain;
            // emcc defaults to C; em++ defaults to C++ and auto-links libc++.
            // Mirror the XCode clang / clang++ split so .c files compile with
            // -std=c11 without hitting "invalid argument '-std=c11' not
            // allowed with 'C++'", while C++ programs still link against libc++.
            this.CompilePath = toolchain.EmccPath!;
            this.LinkPath = toolchain.EmppPath!;
        }

        public CompileResult Compile(TaskEmitter Emitter, Target Target, IArgumentDriver Driver, string? WorkDirectory = null)
        {
            var BuildDatabase = Target.Instance.GetStage<Stages.PrepareBuildDatabasesStage>()!;
            var CompilerArgsDict = Driver.CalculateArguments();
            var CompilerArgsList = CompilerArgsDict.Values.SelectMany(x => x).ToList();
            var DependArgsList = CompilerArgsList.ToList();
            DependArgsList.Add($"COMPILER:ID=Emcc");
            DependArgsList.Add($"COMPILER:VERSION={Version}");

            var SourceFile = Driver.Arguments["Source"] as string;
            var ObjectFile = Driver.Arguments["Object"] as string;
            var Changed = BuildDatabase.GetCompileDatabaseForTarget(Target).RunIfOutdated(Target.Name, SourceFile!, Emitter.Name, (DependencyRecord depend) =>
            {
                ProcessOptions Options = new ProcessOptions
                {
                    Environment = null,
                    WorkingDirectory = WorkDirectory,
                    EnableTimeout = true,
                    TimeoutMilliseconds = 20 * 60 * 1000
                };
                int ExitCode = BS.RunProcess(CompilePath, String.Join(" ", CompilerArgsList), out var OutputInfo, out var ErrorInfo, Options);
                if (ExitCode != 0)
                {
                    throw new TaskFatalError($"Compile {SourceFile} failed with fatal error!", $"emcc: {ErrorInfo}");
                }

                // Emscripten produces MSVC-style /showIncludes output when asked with
                // -MD -MF <depfile>; parse the depfile if the driver populated it.
                if (Driver.Arguments.TryGetValue("SourceDependencies", out var depPathObj) && depPathObj is string depFilePath && File.Exists(depFilePath))
                {
                    var AllLines = File.ReadAllLines(depFilePath)
                        .Select(x => x.Replace("\\ ", " ").Replace(" \\", "").Trim())
                        .ToArray();
                    if (AllLines.Length > 1)
                    {
                        var DepIncludes = new Span<string>(AllLines, 1, AllLines.Length - 1);
                        depend.ExternalFiles.AddRange(DepIncludes);
                    }
                }

                if (!string.IsNullOrEmpty(OutputInfo) && OutputInfo.Contains("warning"))
                    Log.Warning("emcc: {OutputInfo}", OutputInfo);

                depend.ExternalFiles.Add(ObjectFile!);
            }, new List<string> { SourceFile! }, DependArgsList);

            return new CompileResult
            {
                ObjectFile = ObjectFile!,
                PDBFile = "",
                IsRestored = !Changed
            };
        }

        IArgumentDriver ICompiler.CreateArgumentDriver(BuildInstance Instance, CFamily Language, bool isPCH)
            => new EmccCppArgumentDriver(Instance, Toolchain, Language, isPCH);

        public LinkResult Link(TaskEmitter Emitter, Target Target, IArgumentDriver Driver)
        {
            var BuildDatabase = Target.Instance.GetStage<Stages.PrepareBuildDatabasesStage>()!;
            var LinkerArgsDict = Driver.CalculateArguments();
            var LinkerArgsList = LinkerArgsDict.Values.SelectMany(x => x).ToList();
            var DependArgsList = LinkerArgsList.ToList();
            DependArgsList.Add($"LINKER:ID=Emcc");
            DependArgsList.Add($"LINKER:VERSION={Version}");

            var InputFiles = Driver.Arguments["Inputs"] as ArgumentList<string>;
            var OutputFile = Driver.Arguments["Output"] as string;
            var DependencyFiles = new List<string>(InputFiles!);
            for (var i = 0; i < LinkerArgsList.Count; ++i)
            {
                string mapping;
                if (LinkerArgsList[i] == "--embed-file" && i + 1 < LinkerArgsList.Count)
                    mapping = LinkerArgsList[++i];
                else if (LinkerArgsList[i].StartsWith("--embed-file=", StringComparison.Ordinal))
                    mapping = LinkerArgsList[i]["--embed-file=".Length..];
                else
                    continue;
                mapping = mapping.Trim('"');
                var separator = mapping.LastIndexOf('@');
                var source = separator < 0 ? mapping : mapping[..separator];
                if (File.Exists(source))
                    DependencyFiles.Add(source);
            }
            bool Changed = BuildDatabase.GetCompileDatabaseForTarget(Target).RunIfOutdated(Target.Name, OutputFile!, Emitter.Name, (DependencyRecord depend) =>
            {
                ProcessOptions Options = new ProcessOptions
                {
                    Environment = null,
                    EnableTimeout = true,
                    TimeoutMilliseconds = 20 * 60 * 1000
                };
                // Windows CreateProcess caps the command line at ~32K, which
                // we blow past as soon as a target has a few dozen objs. em++
                // supports `@file` response-file syntax just like clang, so
                // unconditionally spill all args to a temp file.
                var rspPath = Path.Combine(Target.GetBuildSrcDepsDir(), $"{Target.Name}.link.rsp");
                Directory.CreateDirectory(Path.GetDirectoryName(rspPath)!);
                File.WriteAllText(rspPath, string.Join("\n", LinkerArgsList));
                int ExitCode = BS.RunProcess(LinkPath, $"@\"{rspPath}\"", out var OutputInfo, out var ErrorInfo, Options);
                if (ExitCode != 0)
                    throw new TaskFatalError($"Link {OutputFile} failed with fatal error!", $"em++ (as linker): {ErrorInfo}");
                else if (!string.IsNullOrEmpty(OutputInfo) && OutputInfo.Contains("warning"))
                    Log.Warning("emcc (as linker): {OutputInfo}", OutputInfo);

                depend.ExternalFiles.Add(OutputFile!);
                // Track the implicit .wasm sidecar produced next to the .js so
                // incremental rebuilds notice when only the wasm binary changes.
                if (OutputFile!.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                {
                    var wasmSidecar = Path.ChangeExtension(OutputFile, ".wasm");
                    if (File.Exists(wasmSidecar))
                        depend.ExternalFiles.Add(wasmSidecar);
                }
            }, DependencyFiles, DependArgsList);

            return new LinkResult
            {
                Target = Target,
                TargetFile = OutputFile!,
                PDBFile = "",
                IsRestored = !Changed
            };
        }

        IArgumentDriver ILinker.CreateArgumentDriver(BuildInstance Instance) => new EmccLinkerArgumentDriver(Instance, Toolchain);

        public Version Version => Toolchain.EmccVersion!;
        // ICompiler.ExecutablePath is used by compile_commands.json generation;
        // surface the compile-role binary (emcc) there.
        public string ExecutablePath => CompilePath;
        private string CompilePath { get; }
        private string LinkPath { get; }
        private Emscripten Toolchain { get; }
    }
}
