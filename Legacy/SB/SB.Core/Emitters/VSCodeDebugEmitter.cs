using SB.Core;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using Serilog;
using System.Text.RegularExpressions;

namespace SB;

using BS = BuildInstance;


// Reference:
//  - https://code.visualstudio.com/docs/debugtest/tasks
//  - https://code.visualstudio.com/docs/cpp/launch-json-reference#_processid




public class VSCodeDebugEmitter : TaskEmitter
{
    // configs
    public string ConfigurationName = "Debug"; // build configurations to filter generated configs
    public string Debugger = "";
    public IToolchain? Toolchain = null;
    public bool PreserveUserConfig = true;

    // path
    public string CmdFilesOutputDir = "";
    public string MergedNatvisOutputDir = "";
    public string WorkspaceRoot = "";
    public string FilterRegex = "";

    // targets to resolve
    private ConcurrentBag<Target> _TargetsToGenerate = new();

    // cache
    private Dictionary<Target, JsonObject> _PerTargetPreLaunchTasks = new();
    private List<JsonObject> _LaunchConfigs = new();

    // emit tasks
    public override bool EnableEmitter(BuildInstance Instance, Target Target) =>
        Target.GetTargetType() == TargetType.Executable ||
        Target.HasAttribute<DebugLaunchAttribute>();
    public override bool EmitTargetTask(BuildInstance Instance, Target Target) => true;
    public override IArtifact? PerTargetTask(BuildInstance Instance, Target Target)
    {
        _TargetsToGenerate.Add(Target);
        return null;
    }

    // generate
    public void Check()
    {
        // check config
        if (Toolchain == null)
        {
            throw new InvalidOperationException("Toolchain is not set for VSCodeDebugEmitter");
        }

        // check path
        if (string.IsNullOrEmpty(CmdFilesOutputDir))
        {
            throw new InvalidOperationException("CmdFilesOutputDir is not set for VSCodeDebugEmitter");
        }
        if (string.IsNullOrEmpty(MergedNatvisOutputDir))
        {
            throw new InvalidOperationException("MergedNatvisOutputDir is not set for VSCodeDebugEmitter");
        }
        if (string.IsNullOrEmpty(WorkspaceRoot))
        {
            throw new InvalidOperationException("WorkspaceRoot is not set for VSCodeDebugEmitter");
        }
    }
    public void Generate()
    {
        Check();

        if (Directory.Exists(CmdFilesOutputDir))
            Directory.Delete(CmdFilesOutputDir, true);
        Directory.CreateDirectory(CmdFilesOutputDir);

        if (string.IsNullOrEmpty(Debugger))
        {
            Debugger = _GetDefaultDebugger();
        }

        // sort targets
        _TargetsToGenerate = new(_TargetsToGenerate.OrderByDescending(t => t.Name));

        // filter targets
        if (!string.IsNullOrEmpty(FilterRegex))
        {
            var regex = new Regex(FilterRegex);
            _TargetsToGenerate = new(_TargetsToGenerate.Where(t => regex.IsMatch(t.Name)));
        }

        // collect tasks
        foreach (var target in _TargetsToGenerate)
        {
            var launch = target.GetAttribute<DebugLaunchAttribute>();

            // get custom pre run cmds
            var preRunCmds = target.GetAttribute<DebugPreRunCommandsAttribute>()?.Commands.ToList() ?? new List<string>();

            var preLaunchTarget = string.IsNullOrWhiteSpace(launch?.PreLaunchBuildTarget)
                ? target.Name
                : launch!.PreLaunchBuildTarget;
            preRunCmds.Insert(0, _MakeBuildCmd(preLaunchTarget, ConfigurationName, Toolchain!.Name));

            // make task json
            var taskJson = _MakeTaskJson(preRunCmds, $"PreRun_{target.Name}_{ConfigurationName}_{Toolchain!.Name}");

            // cache task json
            if (taskJson != null)
                _PerTargetPreLaunchTasks[target] = taskJson;
        }

        // collect launches
        foreach (var target in _TargetsToGenerate)
        {
            // make launch json
            var launchJson = _MakeLaunchJson(target);

            // cache launch json
            _LaunchConfigs.Add(launchJson);
        }

        // add attach config
        {
            var attachConfig = new JsonObject
            {
                ["__SB_TAG__"] = true,
                ["name"] = $"🔗Attach to Process [{ConfigurationName}]",
                ["type"] = Debugger,
                ["request"] = "attach",
                ["processId"] = "${command:pickProcess}",
            };

            if (Debugger == "lldb-dap")
            {
                attachConfig.Remove("processId");
                attachConfig["pid"] = "${command:pickProcess}";
            }

            if (Debugger == "lldb" || Debugger == "lldb-dap")
            {
                attachConfig["sourceMap"] = null;
                attachConfig["env"] = null;
                attachConfig["runInTerminal"] = false;
                if (Debugger == "lldb-dap")
                {
                    attachConfig["stopOnEntry"] = false;
                }
            }
            else if (Debugger == "cppdbg")
            {
                attachConfig["MIMode"] = "gdb";
                attachConfig["miDebuggerPath"] = _FindDebugger("gdb");
                attachConfig["setupCommands"] = Instance.TargetOS == OSPlatform.Linux ? new JsonArray
                {
                    new JsonObject
                    {
                        ["description"] = "Enable pretty-printing",
                        ["text"] = "-enable-pretty-printing",
                        ["ignoreFailures"] = true
                    }
                } : null;
            }

            _LaunchConfigs.Add(attachConfig);
        }

        // generate tasks.json
        {
            // get path
            var taskJsonPath = Path.Combine(WorkspaceRoot, ".vscode", "tasks.json");
            if (!Directory.Exists(Path.GetDirectoryName(taskJsonPath)!))
                Directory.CreateDirectory(Path.GetDirectoryName(taskJsonPath)!);

            JsonObject? taskJsonObj = null;

            // try load existing tasks.json
            if (File.Exists(taskJsonPath) && PreserveUserConfig)
            {
                // load json object
                var jsonText = File.ReadAllText(taskJsonPath);
                taskJsonObj = JsonNode.Parse(jsonText, null, new()
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                })!.AsObject();

                // remove tasks we generated before
                if (taskJsonObj.ContainsKey("tasks") && taskJsonObj["tasks"] is JsonArray tasksArray)
                {
                    _RemoveObjWithTag(tasksArray);
                }
            }

            if (taskJsonObj == null)
            {
                taskJsonObj = new JsonObject
                {
                    ["version"] = "2.0.0",
                    ["tasks"] = JsonSerializer.SerializeToNode(_PerTargetPreLaunchTasks.Values)
                };
            }
            else
            {
                // append new tasks
                foreach (var task in _PerTargetPreLaunchTasks.Values)
                {
                    if (taskJsonObj["tasks"] is JsonArray tasksArray)
                    {
                        tasksArray.Add(task);
                    }
                }
            }

            // write to file
            File.WriteAllText(taskJsonPath, taskJsonObj.ToJsonString(new JsonSerializerOptions
            {
                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
                WriteIndented = true,
                // DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }));
        }

        // generate launch.json
        {
            // get path
            var launchJsonPath = Path.Combine(WorkspaceRoot, ".vscode", "launch.json");
            if (!Directory.Exists(Path.GetDirectoryName(launchJsonPath)!))
                Directory.CreateDirectory(Path.GetDirectoryName(launchJsonPath)!);

            JsonObject? launchJsonObj = null;

            // try load existing launch.json
            if (File.Exists(launchJsonPath) && PreserveUserConfig)
            {
                // load json object
                var jsonText = File.ReadAllText(launchJsonPath);
                launchJsonObj = JsonNode.Parse(jsonText, null, new()
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                })!.AsObject();

                // remove configs we generated before
                if (launchJsonObj.ContainsKey("configurations") && launchJsonObj["configurations"] is JsonArray configsArray)
                {
                    _RemoveObjWithTag(configsArray);
                }
            }

            if (launchJsonObj == null)
            {
                launchJsonObj = new JsonObject
                {
                    ["version"] = "0.2.0",
                    ["configurations"] = JsonSerializer.SerializeToNode(_LaunchConfigs)
                };
            }
            else
            {
                // append new configs
                foreach (var config in _LaunchConfigs)
                {
                    if (launchJsonObj["configurations"] is JsonArray configsArray)
                    {
                        configsArray.Add(config);
                    }
                }
            }

            // write to file
            File.WriteAllText(launchJsonPath, launchJsonObj.ToJsonString(new JsonSerializerOptions
            {
                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
                WriteIndented = true,
                // DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }));
        }
    }
    public void Clear()
    {
        Check();

        // remove task.json
        {
            // get path
            var taskJsonPath = Path.Combine(WorkspaceRoot, ".vscode", "tasks.json");
            if (!Directory.Exists(Path.GetDirectoryName(taskJsonPath)!))
                Directory.CreateDirectory(Path.GetDirectoryName(taskJsonPath)!);
            if (File.Exists(taskJsonPath) && PreserveUserConfig)
            {
                // load json object
                var jsonText = File.ReadAllText(taskJsonPath);
                var taskJsonObj = JsonNode.Parse(jsonText, null, new()
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                })!.AsObject();

                // remove tasks we generated before
                if (taskJsonObj.ContainsKey("tasks") && taskJsonObj["tasks"] is JsonArray tasksArray)
                {
                    _RemoveObjWithTag(tasksArray);
                }

                // write to file
                File.WriteAllText(taskJsonPath, taskJsonObj.ToJsonString(new JsonSerializerOptions
                {
                    TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
                    WriteIndented = true,
                    // DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                }));
            }
        }

        // remove launch.json
        {
            // get path
            var launchJsonPath = Path.Combine(WorkspaceRoot, ".vscode", "launch.json");
            if (!Directory.Exists(Path.GetDirectoryName(launchJsonPath)!))
                Directory.CreateDirectory(Path.GetDirectoryName(launchJsonPath)!);
            if (File.Exists(launchJsonPath) && PreserveUserConfig)
            {
                // load json object
                var jsonText = File.ReadAllText(launchJsonPath);
                var launchJsonObj = JsonNode.Parse(jsonText, null, new()
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                })!.AsObject();

                // remove configs we generated before
                if (launchJsonObj.ContainsKey("configurations") && launchJsonObj["configurations"] is JsonArray configsArray)
                {
                    _RemoveObjWithTag(configsArray);
                }

                // write to file
                File.WriteAllText(launchJsonPath, launchJsonObj.ToJsonString(new JsonSerializerOptions
                {
                    TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
                    WriteIndented = true,
                    // DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                }));
            }
        }

        // remove cmd files
        if (Directory.Exists(CmdFilesOutputDir))
        {
            Directory.Delete(CmdFilesOutputDir, true);
        }

        // remove natvis files
        if (Directory.Exists(MergedNatvisOutputDir))
        {
            Directory.Delete(MergedNatvisOutputDir, true);
        }
    }


    // helpers
    private void _RemoveObjWithTag(JsonArray arr)
    {
        // search obj with __SB_TAG__
        var toRemove = new List<JsonNode>();
        foreach (var task in arr)
        {
            if (task is JsonObject taskObj && taskObj.ContainsKey("__SB_TAG__") && taskObj["__SB_TAG__"]?.GetValue<bool>() == true)
            {
                toRemove.Add(task);
            }
        }

        // remove them
        foreach (var task in toRemove)
        {
            arr.Remove(task);
        }
    }
    private string? _GenerateCmdFile(List<string> cmds, string cmdName)
    {
        if (cmds.Count >= 0)
        {
            // build cmd file content
            var sb = new StringBuilder();
            foreach (var cmd in cmds)
            {
                // push cmd
                sb.AppendLine(cmd);

                // push error break
                sb.AppendLine(BS.HostOS switch
                {
                    OSPlatform.Windows => "IF %ERRORLEVEL% NEQ 0 (exit %ERRORLEVEL%)",
                    OSPlatform.Linux or OSPlatform.OSX => "if [ $? -ne 0 ]; then exit $?; fi",
                    _ => throw new NotSupportedException($"Unsupported OS: {BS.HostOS}"),
                });
            }

            // get cmd file suffix
            var cmdFileExt = BS.HostOS switch
            {
                OSPlatform.Windows => ".bat",
                OSPlatform.Linux or OSPlatform.OSX => ".sh",
                _ => throw new NotSupportedException($"Unsupported OS: {BS.HostOS}"),
            };

            // solve output path
            var outputPath = Path.Combine(CmdFilesOutputDir, $"{cmdName}{cmdFileExt}");

            // try create dir first
            if (!Directory.Exists(CmdFilesOutputDir))
                Directory.CreateDirectory(CmdFilesOutputDir);

            // write to file
            File.WriteAllText(outputPath, sb.ToString());
            return outputPath;
        }
        return null;
    }
    private JsonObject? _MakeTaskJson(List<string> cmds, string cmdName)
    {
        var cmdFilePath = _GenerateCmdFile(cmds, cmdName);
        if (cmdFilePath != null)
        {
            return new JsonObject
            {
                ["__SB_TAG__"] = true,
                ["label"] = cmdName,
                ["type"] = "shell",
                ["command"] = BS.HostOS switch
                {
                    OSPlatform.Windows => cmdFilePath,
                    OSPlatform.OSX or OSPlatform.Linux => $"chmod u+x {cmdFilePath} && {cmdFilePath}",
                    _ => throw new NotSupportedException($"Unsupported OS: {BS.HostOS}"),
                },
                ["options"] = new JsonObject
                {
                    ["cwd"] = "${workspaceFolder}"
                },
                ["group"] = new JsonObject
                {
                    ["kind"] = "build",
                    ["isDefault"] = false
                },
                ["presentation"] = new JsonObject
                {
                    ["echo"] = true,
                    ["reveal"] = "always",
                    ["focus"] = false,
                    ["panel"] = "shared",
                    ["showReuseMessage"] = true,
                    ["clear"] = false,
                    ["close"] = true,
                }
            };
        }
        return null;
    }
    private JsonObject _MakeLaunchJson(Target target)
    {
        // try dump natvis file
        string natvisFileName = "";
        {
            var natvisFiles = _SolveNatvisFiles(target);
            if (natvisFiles.Count != 0)
            {
                var mergedNatvis = MergeNatvis.Merge(natvisFiles);

                if (Directory.Exists(MergedNatvisOutputDir) == false)
                    Directory.CreateDirectory(MergedNatvisOutputDir);

                natvisFileName = Path.Combine(MergedNatvisOutputDir, $"{target.Name}.natvis");
                File.WriteAllText(natvisFileName, mergedNatvis);
            }
        }

        // solve target info
        var launch = target.GetAttribute<DebugLaunchAttribute>();
        var args = launch?.Args.Count > 0
            ? launch.Args
            : target.GetAttribute<DebugArgumentsAttribute>()?.Args ?? new List<string>();
        var displayName = string.IsNullOrWhiteSpace(launch?.DisplayName)
            ? $"{target.Name} [{ConfigurationName}]"
            : $"{launch!.DisplayName} [{ConfigurationName}]";
        var programPath = string.IsNullOrWhiteSpace(launch?.ProgramPath)
            ? _GetTargetProgramPath(target)
            : launch!.ProgramPath;
        var workingDirectory = string.IsNullOrWhiteSpace(launch?.WorkingDirectory)
            ? _GetTargetProgramDir(target)
            : launch!.WorkingDirectory;

        var result = new JsonObject
        {
            ["__SB_TAG__"] = true,
                ["name"] = $"▶️{target.Name} [{ConfigurationName}]",
            ["type"] = Debugger,
            ["request"] = "launch",
            ["program"] = programPath,
            ["args"] = args.Count > 0 ? JsonSerializer.SerializeToNode(args) : new JsonArray(),
            ["cwd"] = workingDirectory,
            ["stopAtEntry"] = false,
            ["console"] = Debugger == "lldb-dap" ? "internalConsole" : "integratedTerminal",
            ["visualizerFile"] = natvisFileName,
            ["preLaunchTask"] = _PerTargetPreLaunchTasks[target]?["label"]?.GetValue<string>(),
            ["autoAttachChildProcess"] = true,
        };
        result["name"] = displayName;

        if (Debugger == "lldb-dap")
        {
            result.Remove("stopAtEntry");
            result["stopOnEntry"] = false;
        }

        if (BuildInstance.HostOS == OSPlatform.OSX)
        {
            if (Debugger != "lldb-dap")
            {
                result["MIMode"] = "lldb";
            }
        }
        else
        {
            if (Debugger == "lldb" || Debugger == "lldb-dap")
            {
                result["sourceMap"] = null;
                result["env"] = null;
                result["runInTerminal"] = false;
                if (Debugger == "lldb-dap")
                {
                    result["stopOnEntry"] = false;
                }
            }
            else if (Debugger == "cppdbg")
            {
                result["MIMode"] = "gdb";
                result["miDebuggerPath"] = _FindDebugger("gdb");
                result["setupCommands"] = target.Instance.TargetOS == OSPlatform.Linux ? new JsonArray
                {
                    new JsonObject
                    {
                        ["description"] = "Enable pretty-printing",
                        ["text"] = "-enable-pretty-printing",
                        ["ignoreFailures"] = true
                    }
                } : null;
            }
        }

        if (launch?.Environment.Count > 0)
        {
            result["env"] = JsonSerializer.SerializeToNode(launch.Environment);
        }

        return result;
    }
    private static string? _FindDebugger(string debuggerName)
    {
        // 在 Windows 上添加 .exe 扩展名
        if (BS.HostOS == OSPlatform.Windows && !debuggerName.EndsWith(".exe"))
            debuggerName += ".exe";

        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        var found = paths.Select(p => Path.Combine(p, debuggerName)).FirstOrDefault(File.Exists);

        // 如果找到了，返回完整路径；否则返回原始名称让系统处理
        return found ?? debuggerName;
    }
    private string _GetTargetProgramPath(Target target)
    {
        var exeName = target.Name + (target.Instance.TargetOS == OSPlatform.Windows ? ".exe" : "");
        return Path.Combine(_GetTargetProgramDir(target), exeName);
    }
    private string _GetTargetProgramDir(Target target)
    {
        return target.GetBinaryDir(ConfigurationName);
        // var relativeBinaryPath = Path.GetRelativePath(WorkspaceRoot, binaryPath);
        // return Path.Combine("${workspaceFolder}", relativeBinaryPath);
    }
    // cppvsdbg, cppdbg, lldb-dap, lldb
    private string _GetDefaultDebugger()
    {
        switch (Instance.TargetOS)
        {
            case OSPlatform.Windows:
                return "cppvsdbg";
            case OSPlatform.Linux:
            case OSPlatform.OSX:
                return "cppdbg"; // gdb
            default:
                return "cppdbg";
        }
    }
    private List<string> _SolveNatvisFiles(Target target)
    {
        var files = new List<string>();

        var _AddNatvisFileForTarget = (Target t) =>
        {
            var natvisList = t.FileList<NatvisFileList>();
            if (natvisList != null)
            {
                foreach (var f in natvisList.Files)
                {
                    if (File.Exists(f))
                        files.Add(Path.GetFullPath(f));
                    else
                        Log.Warning($"Natvis file '{f}' not found for target '{t.Name}'");
                }
            }
        };

        // collect this target natvis files
        _AddNatvisFileForTarget(target);

        // collect dependencies natvis files
        foreach (var depName in target.Dependencies)
        {
            var depTarget = target.Instance.GetTarget(depName);
            if (depTarget != null)
            {
                _AddNatvisFileForTarget(depTarget);
            }
            else
            {
                Log.Warning($"Dependency target '{depName}' not found for target '{target.Name}'");
            }
        }

        return files;
    }
    private string _NormalizeCmdName(string label)
    {
        return Regex.Replace(label, @"[^\w]", "_");
    }
    private string _MakeBuildCmd(string targetName, string configurationName, string toolchain)
    {
        return $"dotnet run --project Legacy/SB/SB.csproj -- build --mode {configurationName.ToLowerInvariant()} --toolchain {toolchain} {targetName}";
    }
}


public class NatvisFileList : FileList
{
}

public class DebugPreRunCommandsAttribute
{
    public List<string> Commands { get; } = new();
}

public class DebugArgumentsAttribute
{
    public List<string> Args { get; set; } = new();
}

public class DebugLaunchAttribute
{
    public string DisplayName { get; set; } = "";
    public string ProgramPath { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string PreLaunchBuildTarget { get; set; } = "";
    public List<string> Args { get; } = new();
    public Dictionary<string, string> Environment { get; } = new();
}

public static partial class TargetExtensions
{
    public static Target DebugPreRunCmd(this Target @this, string cmd)
    {
        var attr = @this.GetAttribute<DebugPreRunCommandsAttribute>();
        if (attr is null)
        {
            attr = new DebugPreRunCommandsAttribute();
            @this.SetAttribute(attr);
        }
        attr.Commands.Add(cmd);
        return @this;
    }
    public static Target DebugArgs(this Target @this, params string[] args)
    {
        @this.SetAttribute(new DebugArgumentsAttribute { Args = args.ToList() }, true);
        return @this;
    }
    public static Target DebugLaunch(this Target @this, DebugLaunchAttribute launch)
    {
        @this.SetAttribute(launch, true);
        return @this;
    }
    public static CppTarget AddNatvisFiles(this CppTarget @this, params string[] files)
    {
        @this.FileList<NatvisFileList>().AddFiles(files);
        return @this;
    }

}
