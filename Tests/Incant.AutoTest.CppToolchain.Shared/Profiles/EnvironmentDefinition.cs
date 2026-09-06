using Incant.Base;
using Incant.Core.Cpp;

namespace Incant.AutoTest.CppToolchain.Shared;

internal sealed class EnvironmentDefinition
{
    internal required string Name { get; init; }

    internal required string Description { get; init; }

    internal required PlatformOS HostOS { get; init; }

    internal required TargetArchitecture HostArchitecture { get; init; }

    internal required string RunnerImage { get; init; }

    internal required IReadOnlyList<InstallationRequirement> Installations { get; init; }
}
