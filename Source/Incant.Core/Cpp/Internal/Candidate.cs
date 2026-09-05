namespace Incant.Core.Cpp;

/// <summary>Keeps discovery priority separate from the authority of version and channel metadata.</summary>
internal sealed class Candidate
{
    internal Candidate(string path, Source source, Version? productVersion = null, Channel? channel = null)
        : this(path, [source], productVersion, channel, productVersion is null ? null : source, channel is null ? null : source)
    {
    }

    private Candidate(string path, IEnumerable<Source> sources, Version? productVersion, Channel? channel,
        Source? versionSource, Source? channelSource)
    {
        Path = SearchPaths.Normalize(path);
        Sources = SearchPaths.Freeze(sources.Distinct().Order());
        ProductVersion = productVersion;
        Channel = channel;
        VersionSource = versionSource;
        ChannelSource = channelSource;
    }

    internal string Path { get; }

    internal Version? ProductVersion { get; }

    internal Channel? Channel { get; }

    internal IReadOnlyList<Source> Sources { get; }

    private Source? VersionSource { get; }

    private Source? ChannelSource { get; }

    internal static IReadOnlyList<Candidate> Merge(IEnumerable<Candidate> candidates) => candidates
        .GroupBy(candidate => candidate.Path, SearchPaths.Comparer)
        .Select(group =>
        {
            Candidate preferred = group.OrderBy(candidate => candidate.Sources.Min()).First();
            Candidate? version = group.Where(candidate => candidate.ProductVersion is not null)
                .OrderBy(candidate => MetadataPriority(candidate.VersionSource)).FirstOrDefault();
            Candidate? channel = group.Where(candidate => candidate.Channel is not null)
                .OrderBy(candidate => MetadataPriority(candidate.ChannelSource)).FirstOrDefault();
            return new Candidate(preferred.Path, group.SelectMany(candidate => candidate.Sources),
                version?.ProductVersion, channel?.Channel, version?.VersionSource, channel?.ChannelSource);
        }).ToArray();

    private static int MetadataPriority(Source? source) => source == Source.Vendor ? -1 : (int?)source ?? int.MaxValue;
}
