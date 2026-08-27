namespace SB.Core
{
    public interface IToolchain
    {
        public static abstract void RegisterSetups(BuildInstance instance);
        public Version Version { get; }
        public ICompiler Compiler { get; }
        public ILinker Linker { get; }
        public IArchiver Archiver { get; }
        public string Name { get; }
    }
}
