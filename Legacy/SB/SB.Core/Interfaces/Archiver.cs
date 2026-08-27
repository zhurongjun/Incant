namespace SB.Core
{
    public struct ArchiveResult : IArtifact
    {
        public string TargetFile { get; init; }
        public bool IsRestored { get; init; }
    }

    public interface IArchiver
    {
        public Version Version { get; }
        public ArchiveResult Archive(TaskEmitter Emitter, Target Target, IArgumentDriver Driver);
        public IArgumentDriver CreateArgumentDriver(BuildInstance Instance);
    }
}
