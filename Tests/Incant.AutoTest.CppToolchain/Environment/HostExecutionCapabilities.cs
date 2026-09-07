using Incant.Core.Cpp;

namespace Incant.AutoTest.CppToolchain;

/// <summary>The ordered host architectures proven runnable during preflight.</summary>
internal sealed class HostExecutionCapabilities
{
    internal HostExecutionCapabilities(
        IEnumerable<TargetArchitecture> architectures)
    {
        ArgumentNullException.ThrowIfNull(architectures);
        Architectures = Array.AsReadOnly(architectures
            .Where(architecture =>
                architecture != TargetArchitecture.Unknown)
            .Distinct()
            .ToArray());
        if (Architectures.Count == 0)
        {
            throw new ArgumentException(
                "At least one known host architecture is required.",
                nameof(architectures));
        }
    }

    internal IReadOnlyList<TargetArchitecture> Architectures { get; }

    internal bool CanExecute(TargetArchitecture architecture) =>
        Architectures.Contains(architecture);
}
