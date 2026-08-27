using System.Text;
using System.Security.Cryptography;
using System.Diagnostics;
using SB.Core;
using Serilog;

namespace SB
{
    using BS = BuildInstance;

    public struct ProcessOptions
    {
        public Dictionary<string, string?>? Environment { get; set; } = null;
        public string? WorkingDirectory { get; set; } = null;
        public bool EnableTimeout { get; set; } = false;
        public int TimeoutMilliseconds { get; set; } = 20 * 60 * 1000; // Default to 20 minutes
        public static Lazy<ProcessOptions> Default => new(() => new ProcessOptions());
        public ProcessOptions() { }
    }
    
    public partial class BuildInstance
    {
        public static string GetUniqueTempFileName(string File, string Hint, string Extension, IEnumerable<string>? Args = null)
        {
            string FullIdentifier = File + (Args is null ? "" : String.Join("", Args));
            // var SHA = SHA256.HashData(Encoding.UTF8.GetBytes(FullIdentifier));
            var MD5Code = MD5.HashData(Encoding.UTF8.GetBytes(FullIdentifier));
            return $"{Hint}_{Path.GetFileName(File)}_{Convert.ToHexString(MD5Code)}.{Extension}";
        }

        public static bool CheckPath(string P, bool MustExist) => Path.IsPathFullyQualified(P) && (!MustExist || Directory.Exists(P));
        public static bool CheckFile(string P, bool MustExist) => Path.IsPathFullyQualified(P) && (!MustExist || File.Exists(P));

        public static string QuoteCommandLineArgument(string Argument)
        {
            // RunProcess 当前仍把所有参数拼成一整段命令行字符串传给 ProcessStartInfo.Arguments。
            // 因此这里引用的是“已经成形的 argv 片段”，而不是改变进程启动模型；这样可以把修复范围
            // 控制在各个 ArgumentDriver 生成路径参数的边界上。
            var Builder = new StringBuilder(Argument.Length + 2);
            Builder.Append('"');

            var BackslashCount = 0;
            foreach (var Character in Argument)
            {
                if (Character == '\\')
                {
                    BackslashCount++;
                    continue;
                }

                if (Character == '"')
                {
                    Builder.Append('\\', BackslashCount * 2 + 1);
                    Builder.Append('"');
                }
                else
                {
                    Builder.Append('\\', BackslashCount);
                    Builder.Append(Character);
                }

                BackslashCount = 0;
            }

            // Windows 命令行解析会把结尾的反斜杠视为右引号的转义候选；引用路径时如果不成倍补齐，
            // 形如 C:\dir\ 的参数会吞掉闭合引号，后续参数也会被串进去。
            Builder.Append('\\', BackslashCount * 2);
            Builder.Append('"');
            return Builder.ToString();
        }

        public static string QuoteCommandLinePath(string Path) => QuoteCommandLineArgument(Path);

        public static string QuoteCommandLineArgumentIfNeeded(string Argument)
        {
            return Argument.Any(char.IsWhiteSpace) ? QuoteCommandLineArgument(Argument) : Argument;
        }

        public static int RunProcess(string ExecutablePath, string Arguments, out string Output, out string Error)
        {
            return RunProcess(ExecutablePath, Arguments, out Output, out Error, ProcessOptions.Default.Value);
        }

        public static int RunProcess(string ExecutablePath, string Arguments, out string Output, out string Error, ProcessOptions options)
        {
            return RunProcessCore(
                ExecutablePath,
                startInfo => startInfo.Arguments = Arguments,
                out Output,
                out Error,
                options);
        }

        public static int RunProcess(string ExecutablePath, IReadOnlyList<string> Arguments, out string Output, out string Error, ProcessOptions options)
        {
            return RunProcessCore(
                ExecutablePath,
                startInfo =>
                {
                    foreach (var argument in Arguments)
                        startInfo.ArgumentList.Add(argument);
                },
                out Output,
                out Error,
                options);
        }

        private static int RunProcessCore(
            string ExecutablePath,
            Action<ProcessStartInfo> SetArguments,
            out string Output,
            out string Error,
            ProcessOptions options)
        {
            using (Profiler.BeginZone($"RunProcess", color: (uint)Profiler.ColorType.Yellow1))
            {
                if (!OperatingSystem.IsWindows() && File.Exists(ExecutablePath))
                {
                    var Mode = File.GetUnixFileMode(ExecutablePath);
                    if (!Mode.HasFlag(UnixFileMode.UserExecute))
                        File.SetUnixFileMode(ExecutablePath, UnixFileMode.UserExecute);
                }

                string displayedArguments = "";
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = ExecutablePath,
                        RedirectStandardInput = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = false,
                        UseShellExecute = false,
                        WorkingDirectory = options.WorkingDirectory ?? Directory.GetParent(ExecutablePath)!.FullName
                    };
                    SetArguments(startInfo);
                    displayedArguments = startInfo.ArgumentList.Count > 0
                        ? string.Join(" ", startInfo.ArgumentList)
                        : startInfo.Arguments;

                    using Process P = new Process
                    {
                        StartInfo = startInfo
                    };

                    if (options.Environment is not null)
                    {
                        foreach (var kvp in options.Environment)
                        {
                            if (kvp.Value is null)
                                P.StartInfo.Environment.Remove(kvp.Key);
                            else
                                P.StartInfo.Environment[kvp.Key] = kvp.Value;
                        }
                    }

                    StringBuilder localOutput = new();
                    StringBuilder localError = new();
                    P.OutputDataReceived += (sender, e) => { if (e.Data is not null) localOutput.AppendLine(e.Data); };
                    P.ErrorDataReceived += (sender, e) => { if (e.Data is not null) localError.AppendLine(e.Data); };
                    P.Start();
                    using var cancellationRegistration = _processCancellation.Value.Register(() =>
                    {
                        try
                        {
                            if (!P.HasExited)
                                P.Kill(true);
                        }
                        catch
                        {
                        }
                    });
                    P.BeginOutputReadLine();
                    P.BeginErrorReadLine();

                    bool exited;
                    if (options.EnableTimeout)
                    {
                        exited = P.WaitForExit(options.TimeoutMilliseconds);
                        if (!exited)
                        {
                            try
                            {
                                P.Kill(true);
                            }
                            catch { }
                            P.WaitForExit();
                            Output = localOutput.ToString();
                            Error = "TimeOut";
                            Log.Error("Process {ExecutablePath} with arguments {Arguments} timed out after {TimeoutMilliseconds} milliseconds.", ExecutablePath, displayedArguments, options.TimeoutMilliseconds);
                            return -1;
                        }
                    }
                    P.WaitForExit();
                    _processCancellation.Value.ThrowIfCancellationRequested();
                    Output = localOutput.ToString();
                    Error = localError.ToString();
                    return P.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    throw new TaskFatalError($"Failed to run process {ExecutablePath} with arguments {displayedArguments}", e.Message);
                }
            }
        }
    }

    public static class StringExtensions
    {
        public static string ToUpperSnakeCase(this string text)
        {
            if(text == null) {
                throw new ArgumentNullException(nameof(text));
            }
            if(text.Length < 2) {
                return text.ToUpperInvariant();
            }
            var sb = new StringBuilder();
            sb.Append(char.ToUpperInvariant(text[0]));
            for(int i = 1; i < text.Length; ++i) {
                char c = text[i];
                if(char.IsUpper(c)) {
                    sb.Append('_');
                    sb.Append(char.ToUpperInvariant(c));
                } else {
                    sb.Append(char.ToUpperInvariant(c));
                }
            }
            return sb.ToString();
        }

        public static bool Is_C_Cpp(this string p) => p.EndsWith(".c") || p.EndsWith(".cpp") || p.EndsWith(".cc") || p.EndsWith(".cxx");
        public static bool Is_OC_OCpp(this string p) => p.EndsWith(".m") || p.EndsWith(".mm") || p.EndsWith(".mpp");
    }
}
