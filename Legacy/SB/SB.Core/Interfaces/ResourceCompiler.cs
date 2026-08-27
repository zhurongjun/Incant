namespace SB.Core
{
    public struct ResourceCompileResult : IArtifact
    {
        public string ResourceFile { get; init; }
        public bool IsRestored { get; init; }
    }

    public interface IResourceCompiler
    {
        public Version Version { get; }
        public string ExecutablePath { get; }
        public IArgumentDriver CreateArgumentDriver(BuildInstance Instance);
        public ResourceCompileResult Compile(TaskEmitter Emitter, Target Target, IArgumentDriver Driver, string? WorkDirectory = null);
    }
}
