using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace SB.Core
{
    using ArgumentName = string;

    public enum PDBMode
    {
        Disable,
        Embed,
        Standalone
    }

    public enum OptimizationLevel
    {
        None,
        Fast,
        Faster,
        Fastest,
        Smallest
    }

    public enum FpModel
    {
        Precise,
        Fast,
        Strict
    }

    public enum WarningLevel
    {
        None,
        All,
        AllExtra,
        Everything
    }

    public enum TargetType
    {
        HeaderOnly,
        Objects,
        Static,
        Dynamic,
        Executable
    }

    public enum Visibility
    {
        Private,
        Public,
        Interface
    }

    public struct CompileCommand
    {
        public string directory { get; init; }
        public List<string> arguments { get; init; }
        public string file { get; init; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class ArgumentDriverAttribute : Attribute
    {
        public Type InjectType { get; init; } = typeof(Target);
    }

    public interface IArgumentDriver
    {
        public Dictionary<ArgumentName, string[]> CalculateArguments()
        {
            Dictionary<ArgumentName, string[]> Args = new();
            var DriverType = GetType();
            foreach (var Method in DriverType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (Arguments.TryGetValue(Method.Name, out var ArgumentValue))
                {
                    try
                    {
                        var Result = Method.Invoke(this, new object[] { ArgumentValue! });
                        if (Result is string)
                            Args.Add(Method.Name, new string[] { (string)Result! });
                        if (Result is string[])
                            Args.Add(Method.Name, (string[])Result);
                        else if (Result is IEnumerable<string>)
                            Args.Add(Method.Name, ((IEnumerable<string>)Result).ToArray());
                    }
                    catch (TargetInvocationException TIE)
                    {
                        if (TIE.InnerException is TaskFatalError TFE)
                            throw TFE;
                    }
                }
            }
            Args.Add("RAW", RawArguments.ToArray());
            return Args;
        }

        public string CompileCommands(ICompiler Compiler, string directory)
        {
            CompileCommand compile_commands = new CompileCommand
            {
                directory = directory,
                arguments = CalculateArguments().Values.SelectMany(x => x).ToList(),
                file = (string)Arguments["Source"]!
            };
            compile_commands.arguments.Insert(0, Compiler.ExecutablePath);
            return Json.Serialize(compile_commands);
        }

        public IArgumentDriver AddArgument(ArgumentName key, object value)
        {
            Arguments.Add(key, value);
            return this;
        }

        public IArgumentDriver AddArguments(IReadOnlyDictionary<ArgumentName, object?>? Args)
        {
            Arguments.AddRange(Args);
            return this;
        }

        public IArgumentDriver MergeArguments(ArgumentDictionary? Args, bool AllowOverride)
        {
            Arguments.Merge(Args, AllowOverride);
            return this;
        }

        public IArgumentDriver AddRawArgument(string Arg)
        {
            RawArguments.Add(Arg);
            return this;
        }

        public ArgumentDictionary Arguments { get; }
        public HashSet<string> RawArguments { get; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class TargetPropertyAttribute : Attribute
    {
        public bool InheritBehavior { get; init; }
        public bool PathBehavior { get; init; }
    }
}