using System.Text.Json;
using System.Text.Json.Serialization;

namespace Incant.AutoTest.CppToolchain.Setup;

internal enum ProbeKind
{
    File,
    Directory,
}

internal sealed record InstallationProbe(string RelativePath, ProbeKind Kind);

internal static class ComponentCompletionStore
{
    private const string CompletionFile = ".incant-component-ready.json";
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static async Task<bool> IsReadyAsync(
        string root,
        string componentId,
        string fingerprint,
        IReadOnlyList<InstallationProbe> probes,
        CancellationToken cancellationToken)
    {
        string markerPath = Path.Combine(root, CompletionFile);
        if (!File.Exists(markerPath) || !ProbesExist(root, probes))
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
                && marker.Probes.SequenceEqual(probes);
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
        if (!ProbesExist(root, probes))
        {
            throw new InvalidDataException(
                $"Component '{componentId}' failed one or more completion probes below '{root}'.");
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

    internal static bool ProbesExist(
        string root,
        IReadOnlyList<InstallationProbe> probes)
    {
        foreach (InstallationProbe probe in probes)
        {
            string path = Path.Combine(root, NormalizeRelativePath(probe.RelativePath));
            bool exists = probe.Kind switch
            {
                ProbeKind.File => File.Exists(path),
                ProbeKind.Directory => Directory.Exists(path),
                _ => throw new ArgumentOutOfRangeException(nameof(probes), probe.Kind, null),
            };
            if (!exists)
            {
                return false;
            }
        }

        return probes.Count > 0;
    }

    internal static string NormalizeRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
        {
            throw new ArgumentException(
                $"Probe path '{path}' must be relative.",
                nameof(path));
        }

        string basePath = Path.Combine(Path.GetTempPath(), "incant-probe-root");
        string normalized = Path.GetFullPath(path, basePath);
        string relative = Path.GetRelativePath(basePath, normalized);
        if (relative.Length == 0
            || relative == "."
            || Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Probe path '{path}' must remain inside the installation root.",
                nameof(path));
        }

        return relative;
    }

    private sealed class CompletionMarker
    {
        public required int SchemaVersion { get; init; }

        public required string ComponentId { get; init; }

        public required string Fingerprint { get; init; }

        public required IReadOnlyList<InstallationProbe> Probes { get; init; }
    }
}
