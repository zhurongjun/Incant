namespace Incant.Core.Cpp.FindSdk;

/// <summary>Builds an ordered inventory and computes ownership after all component roots are known.</summary>
internal sealed class ResourceCollector
{
    private readonly List<(ResourcePurpose Purpose, string Path, int? ApiLevel)> _entries = [];

    private readonly List<string> _ownedRoots = [];

    internal ResourceCollector(params string[] ownedRoots)
    {
        foreach (string root in ownedRoots)
        {
            Own(root);
        }
    }

    internal void Own(string root)
    {
        string normalized = SearchPaths.Normalize(root);
        if (!_ownedRoots.Contains(normalized, SearchPaths.Comparer))
        {
            _ownedRoots.Add(normalized);
        }
    }

    internal bool Owns(string path) => _ownedRoots.Any(root => SearchPaths.Contains(root, SearchPaths.Normalize(path)));

    internal void Add(ResourcePurpose purpose, string path, int? apiLevel = null)
    {
        bool isFile = purpose is ResourcePurpose.Library or ResourcePurpose.Startup;
        string normalized = SearchPaths.Normalize(path);
        if (!(isFile ? File.Exists(normalized) : Directory.Exists(normalized)))
        {
            return;
        }

        if (!_entries.Any(entry => entry.Purpose == purpose && entry.ApiLevel == apiLevel
            && SearchPaths.Comparer.Equals(entry.Path, normalized)))
        {
            _entries.Add((purpose, normalized, apiLevel));
        }
    }

    internal void AddRange(IEnumerable<Resource> resources)
    {
        foreach (Resource resource in resources)
        {
            Add(resource.Purpose, resource.Path, resource.ApiLevel);
        }
    }

    internal IReadOnlyList<Resource> Build() => _entries.Select(entry =>
        new Resource(entry.Purpose, entry.Path, !_ownedRoots.Any(root => SearchPaths.Contains(root, entry.Path)), entry.ApiLevel)).ToList().AsReadOnly();
}
