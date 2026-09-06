using System.Text.Json;
using System.Text.Json.Serialization;

namespace Incant.AutoTest.CppToolchain;

internal sealed class EnvironmentManifest
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public required int SchemaVersion { get; init; }

    public required string Profile { get; init; }

    public required RunnerManifest Runner { get; init; }

    public required IReadOnlyDictionary<string, string?> Environment { get; init; }

    public required IReadOnlyList<InstallationManifest> Installations { get; init; }

    public required IReadOnlyList<RuntimeManifest> Runtimes { get; init; }

    internal static async Task<EnvironmentManifest> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<EnvironmentManifest>(
            stream, s_jsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("The environment manifest is empty.");
    }
}

internal sealed class RunnerManifest
{
    public required string ImageLabel { get; init; }

    public required string ImageOS { get; init; }

    public required string ImageVersion { get; init; }

    public required string OS { get; init; }

    public required string Architecture { get; init; }
}

internal sealed class InstallationManifest
{
    public required string Id { get; init; }

    public required InstallationKind Kind { get; init; }

    public required string RootPath { get; init; }

    public required string Version { get; init; }

    public required IReadOnlyDictionary<string, string?> Environment { get; init; }

    public required string? SourceUri { get; init; }

    public required string? Sha256 { get; init; }

    public required string? Revision { get; init; }
}

internal sealed class RuntimeManifest
{
    public required string Id { get; init; }

    public required RuntimeKind Kind { get; init; }

    public required string Path { get; init; }

    public required string Version { get; init; }

    public required string? InstallationId { get; init; }

    public required string? SourceUri { get; init; }

    public required string? Sha256 { get; init; }

    public required string? Revision { get; init; }
}
