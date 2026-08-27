using System.IO;

namespace SB.Core
{
    using ArgumentName = string;
    using VS = VisualStudio;
    using BS = BuildInstance;
    [ArgumentDriver(InjectType = typeof(CppTarget))]
    public class ARArgumentDriver : IArgumentDriver
    {
        [TargetProperty] 
        public string TargetType(TargetType type) => typeMap.TryGetValue(type, out var t) ? t : throw new ArgumentException($"Invalid target type \"{type}\" for clang++ Linker!");
        static readonly Dictionary<TargetType, string> typeMap = new Dictionary<TargetType, string> { { Core.TargetType.Static, "" } };

        public string[] Inputs(ArgumentList<string> inputs) => inputs.Select(dir => $"{dir}").ToArray();

        public string Output(string output) => BS.CheckFile(output, false) ? $"-cr {output}" : throw new ArgumentException($"Invalid output file path {output}!");

        public ArgumentDictionary Arguments { get; } = new ArgumentDictionary();
        public HashSet<string> RawArguments { get; } = new HashSet<string> {  };
    }
}