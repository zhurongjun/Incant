namespace SB.Core
{
    using BS = BuildInstance;

    [ArgumentDriver(InjectType = typeof(CppTarget))]
    public class EmarArgumentDriver : IArgumentDriver
    {
        [TargetProperty]
        public string TargetType(TargetType type) => typeMap.TryGetValue(type, out var t) ? t : throw new ArgumentException($"Invalid target type \"{type}\" for emar!");
        static readonly Dictionary<TargetType, string> typeMap = new Dictionary<TargetType, string> { { Core.TargetType.Static, "" } };

        public string[] Inputs(ArgumentList<string> inputs) => inputs.Select(f => $"{f}").ToArray();

        public string Output(string output) => BS.CheckFile(output, false) ? $"-cr {output}" : throw new ArgumentException($"Invalid output file path {output}!");

        public ArgumentDictionary Arguments { get; } = new ArgumentDictionary();
        public HashSet<string> RawArguments { get; } = new HashSet<string>();
    }
}
