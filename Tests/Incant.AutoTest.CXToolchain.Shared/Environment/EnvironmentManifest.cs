using System.Text.Json;
using System.Text.Json.Serialization;

namespace Incant.AutoTest.CXToolchain.Shared;

internal sealed class EnvironmentManifest
{
    internal const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
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

    internal static async Task SaveAsync(
        string path,
        EnvironmentManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(manifest);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The environment manifest path has no parent directory.", nameof(path));
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = fullPath + $".{System.Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream, manifest, s_jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
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
