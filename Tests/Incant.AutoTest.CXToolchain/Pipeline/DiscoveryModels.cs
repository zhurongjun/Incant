using Incant.CX;
using Sdk = Incant.CX.FindSdk.Sdk;
using ToolSet = Incant.CX.FindTools.ToolSet;

namespace Incant.AutoTest.CXToolchain;

internal enum DiscoverySubject
{
    ToolSets,
    Sdks,
}

internal sealed class DiscoveryProbe
{
    internal required string Name { get; init; }

    internal required DiscoverySubject Subject { get; init; }

    internal required string Query { get; init; }

    internal bool Succeeded { get; set; }

    internal bool Completed => Succeeded || Error is not null;

    internal string? Error { get; set; }

    internal IReadOnlyList<ToolSet> ToolSets { get; set; } = [];

    internal IReadOnlyList<Sdk> Sdks { get; set; } = [];

    internal IReadOnlyList<Diagnostic> Diagnostics { get; set; } = [];
}

internal sealed class InstallationDiscovery(
    InstallationRequirement requirement,
    InstallationManifest manifest,
    bool managed = true)
{
    internal bool Managed { get; } = managed;

    internal InstallationRequirement Requirement { get; } = requirement;

    internal InstallationManifest Manifest { get; } = manifest;

    internal List<ToolSet> ToolSets { get; } = [];

    internal List<Sdk> Sdks { get; } = [];

    internal List<string> Decisions { get; } = [];

    internal List<string> Failures { get; } = [];

    internal bool Succeeded => Failures.Count == 0;
}
