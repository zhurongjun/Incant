
using System.Runtime.CompilerServices;
using SB.Core;

namespace SB
{    
    public partial class ArgumentDictionary : Dictionary<string, object?>
    {
        public ArgumentDictionary([CallerFilePath] string? path = null) { this.Directory = Path.GetDirectoryName(path)!; }
        public void AppendToArgumentList<T>(string Name, params T[] Args) {{ this.GetOrAddNew<string, ArgumentList<T>>(Name).AddRange(Args); }}
        public void OverrideArgument<T>(string Name, T value) {{ this.Override(Name, value); }}
        public string HandlePath(string path) => Path.IsPathFullyQualified(path) ? path : Path.Combine(Directory!, path);
        public string[] HandlePath(string[] paths)
        {
            for (int i = 0; i < paths.Length; i++)
                paths[i] = HandlePath(paths[i]);
            return paths;
        }
        protected string? Directory;
    }

    public abstract partial class TargetSetters
    {
        protected TargetSetters(string Location)
        {
            this.Directory = Path.GetDirectoryName(Location)!;
            FinalArguments = new (Location);
            PublicArguments = new (Location);
            PrivateArguments = new (Location);
            InterfaceArguments = new (Location);
        }

        public ArgumentDictionary GetArgumentsContainer(Visibility Visibility)
        {
            switch (Visibility)
            {
                case Visibility.Public: return PublicArguments;
                case Visibility.Private: return PrivateArguments;
                case Visibility.Interface: return InterfaceArguments;
            }
            return PrivateArguments;
        }

        public IReadOnlyDictionary<string, object?> Arguments => FinalArguments;
        public ArgumentDictionary FinalArguments { get; }
        public ArgumentDictionary PublicArguments { get; }
        public ArgumentDictionary PrivateArguments { get; }
        public ArgumentDictionary InterfaceArguments { get; } 
        public string Directory { get; }
    }
}