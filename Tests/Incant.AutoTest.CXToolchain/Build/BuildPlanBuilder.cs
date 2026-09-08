using Incant.CX.Arguments;

namespace Incant.AutoTest.CXToolchain;

internal sealed class BuildPlanBuilder(
    string workDirectory,
    IReadOnlyDictionary<string, string?> environment,
    ResponseFileDialect responseDialect = ResponseFileDialect.Gnu)
{
    private readonly List<BuildAction> _actions = [];

    internal string WorkDirectory { get; } = workDirectory;

    internal string PathFor(string name) => Path.Combine(WorkDirectory, name);

    internal void Add(
        string id,
        BuildActionPhase phase,
        string executable,
        IEnumerable<string> arguments,
        IEnumerable<string>? dependencies = null,
        IEnumerable<string>? artifacts = null,
        IEnumerable<string>? outputFragments = null,
        TimeSpan? timeout = null,
        IReadOnlyList<string>? recreatedArtifacts = null)
    {
        _actions.Add(new BuildAction(
            id,
            phase,
            executable,
            arguments.ToArray(),
            WorkDirectory,
            environment,
            (dependencies ?? []).ToArray(),
            (artifacts ?? []).ToArray(),
            (outputFragments ?? []).ToArray(),
            timeout ?? TimeSpan.FromMinutes(2))
        {
            ResponseDialect = phase == BuildActionPhase.Build ? responseDialect : null,
            RecreatedArtifacts = recreatedArtifacts ?? [],
        });
    }

    internal BuildPlan Build()
    {
        string[] duplicateIds = _actions.GroupBy(action => action.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateIds.Length > 0)
        {
            throw new InvalidOperationException(
                $"Build action ids must be unique: {string.Join(", ", duplicateIds)}.");
        }

        if (!_actions.Any(action => action.Phase == BuildActionPhase.Build))
        {
            throw new InvalidOperationException(
                "A build plan must contain at least one build action.");
        }

        bool reachedExecution = false;
        var previousIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (BuildAction action in _actions)
        {
            reachedExecution |= action.Phase == BuildActionPhase.Execute;
            if (reachedExecution && action.Phase == BuildActionPhase.Build)
            {
                throw new InvalidOperationException(
                    $"Build action '{action.Id}' appears after execution has started.");
            }

            string? invalidDependency = action.Dependencies.FirstOrDefault(
                dependency => !previousIds.Contains(dependency));
            if (invalidDependency is not null)
            {
                throw new InvalidOperationException(
                    $"Action '{action.Id}' depends on later or unknown action '{invalidDependency}'.");
            }

            previousIds.Add(action.Id);
        }

        return new BuildPlan
        {
            WorkDirectory = WorkDirectory,
            Actions = _actions.ToArray(),
        };
    }
}
