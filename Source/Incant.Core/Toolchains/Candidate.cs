namespace Incant.Core.Toolchains;

internal sealed class Candidate
{
    private readonly List<Source> _sources;

    internal Candidate(string path, Source source)
    {
        Path = path;
        _sources = [source];
    }

    internal string Path { get; }

    internal IReadOnlyList<Source> Sources => _sources;

    internal void AddSource(Source source)
    {
        if (_sources.Contains(source))
        {
            return;
        }

        _sources.Add(source);
        _sources.Sort((left, right) =>
            Resolver.GetSourcePriority(left).CompareTo(
                Resolver.GetSourcePriority(right)));
    }
}
