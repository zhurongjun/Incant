using SB.Core;

namespace SB;

public sealed class BeforeBuildEmitter : TaskEmitter
{
    public override bool EnableEmitter(BuildInstance instance, Target target)
        => target.HasBeforeBuildActions();

    public override bool EmitTargetTask(BuildInstance instance, Target target)
        => true;

    public override IArtifact? PerTargetTask(BuildInstance instance, Target target)
    {
        target.CallBeforeBuildActions();
        return new PlainArtifact { IsRestored = false };
    }
}
