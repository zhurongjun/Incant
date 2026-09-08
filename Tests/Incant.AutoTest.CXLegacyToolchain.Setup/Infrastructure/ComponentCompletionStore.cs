using System.Text.Json;
using System.Text.Json.Serialization;

namespace Incant.AutoTest.CXLegacyToolchain.Setup;

internal static class ComponentCompletionStore
{
    private const string CompletionFile = ".incant-component-ready.json";
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static async Task<bool> MatchesAsync(
        string root,
        string componentId,
        string fingerprint,
        IReadOnlyList<InstallationProbe> probes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probes);
        if (probes.Count == 0)
        {
            return false;
        }

        string markerPath = Path.Combine(root, CompletionFile);
        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            await using FileStream stream = File.OpenRead(markerPath);
            CompletionMarker? marker = await JsonSerializer.DeserializeAsync<CompletionMarker>(
                stream, s_jsonOptions, cancellationToken).ConfigureAwait(false);
            return marker is not null
                && marker.SchemaVersion == 1
                && string.Equals(marker.ComponentId, componentId, StringComparison.Ordinal)
                && string.Equals(marker.Fingerprint, fingerprint, StringComparison.Ordinal)
                && marker.Probes is not null
                && ProbesEqual(marker.Probes, probes);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return false;
        }
    }

    internal static async Task WriteAsync(
        string root,
        string componentId,
        string fingerprint,
        IReadOnlyList<InstallationProbe> probes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probes);
        if (probes.Count == 0)
        {
            throw new ArgumentException(
                "At least one completion probe is required.",
                nameof(probes));
        }

        string markerPath = Path.Combine(root, CompletionFile);
        string temporary = markerPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var marker = new CompletionMarker
        {
            SchemaVersion = 1,
            ComponentId = componentId,
            Fingerprint = fingerprint,
            Probes = probes.ToArray(),
        };
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream, marker, s_jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, markerPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static bool ProbesEqual(
        IReadOnlyList<InstallationProbe?> left,
        IReadOnlyList<InstallationProbe> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; ++index)
        {
            InstallationProbe? leftProbe = left[index];
            InstallationProbe rightProbe = right[index];
            if (leftProbe is null
                || !string.Equals(
                    leftProbe.RelativePath,
                    rightProbe.RelativePath,
                    StringComparison.Ordinal)
                || leftProbe.Kind != rightProbe.Kind
                || !(leftProbe.Arguments ?? []).SequenceEqual(
                    rightProbe.Arguments ?? [],
                    StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class CompletionMarker
    {
        public required int SchemaVersion { get; init; }

        public required string ComponentId { get; init; }

        public required string Fingerprint { get; init; }

        public required IReadOnlyList<InstallationProbe?> Probes { get; init; }
    }
}
