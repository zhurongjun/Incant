using SB.Core;
using System.Collections.Generic;
using System.Threading;

namespace SB
{
    public enum DependencyModel
    {
        PerTarget,
        ExternalTarget
    }

    public abstract class TaskEmitter
    {
        public BuildInstance Instance => _instance ?? throw new InvalidOperationException($"Emitter {SelfName} is not attached to a build instance.");
        public int ElapsedMilliseconds => _elapsedMilliseconds;
        private BuildInstance? _instance;
        private int _elapsedMilliseconds;

        public TaskEmitter AddDependency(string EmitterName, DependencyModel Model)
        {
            Dependencies.Add(new KeyValuePair<string, DependencyModel>(EmitterName, Model));
            return this;
        }

        public TaskEmitter AddTargetDependency(string TargetName, string EmitterName)
        {
            TargetDependencies.Add(new KeyValuePair<string, string>(TargetName, EmitterName));
            return this;
        }

        public virtual void RegisterSetups(BuildInstance Instance) { }

        public virtual bool EnableEmitter(BuildInstance Instance, Target Target) => false;

        public virtual bool EmitTargetTask(BuildInstance Instance, Target Target) => false;
        public virtual IArtifact? PerTargetTask(BuildInstance Instance, Target Target) => null;

        public virtual bool EmitFileTask(BuildInstance Instance, Target Target, FileList FileList) => false;
        public virtual IArtifact? PerFileTask(BuildInstance Instance, Target Target, FileList FileList, FileOptions? Options, string File) => null;

        public string Name => SelfName;

        internal void Attach(BuildInstance instance, string name)
        {
            if (_instance is not null && !ReferenceEquals(_instance, instance))
                throw new InvalidOperationException($"Emitter {SelfName} is already attached to another build instance.");

            _instance = instance;
            SelfName = name;
        }

        protected void AddElapsedMilliseconds(long elapsedMilliseconds)
        {
            Interlocked.Add(ref _elapsedMilliseconds, (int)elapsedMilliseconds);
        }

        internal string SelfName = "";
        internal HashSet<KeyValuePair<string, DependencyModel>> Dependencies { get; } = new();
        internal HashSet<KeyValuePair<string, string>> TargetDependencies { get; } = new();
    }
}
