using System.Formats.Tar;
using System.IO.Compression;

namespace Incant.AutoTest.CXToolchain.Setup;

internal sealed class ArchiveInstaller(SetupContext context)
{
    private const string ArchiveContractVersion = "2";

    internal async Task<string> InstallAsync(
        string componentId,
        string archive,
        string destination,
        IReadOnlyList<InstallationProbe> probes,
        string sha256,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(componentId);
        ArgumentNullException.ThrowIfNull(probes);
        if (probes.Count == 0)
        {
            throw new ArgumentException("At least one archive probe is required.", nameof(probes));
        }

        string resolvedDestination = context.Paths.AssertChild(destination);
        string fingerprint = "archive-contract:"
            + ArchiveContractVersion
            + $";sha256:{sha256.ToLowerInvariant()}";
        if (await IsReadyAsync(
            resolvedDestination,
            componentId,
            fingerprint,
            probes,
            cancellationToken).ConfigureAwait(false))
        {
            Console.WriteLine(
                $"[archive:cache-hit] destination={resolvedDestination} "
                + $"probes={string.Join(",", probes.Select(probe => probe.RelativePath))}");
            return SetupPathGuard.RequireDirectory(resolvedDestination, "archive destination");
        }

        string staging = context.Paths.AssertChild(
            resolvedDestination + $".extracting.{Guid.NewGuid():N}");
        string ready = context.Paths.AssertChild(
            resolvedDestination + $".ready.{Guid.NewGuid():N}");
        context.Paths.ResetDirectory(staging);
        try
        {
            await ExtractAsync(archive, staging, cancellationToken).ConfigureAwait(false);
            string? source = LocateArchiveRoot(staging, probes[0]);
            if (source is null)
            {
                throw new InvalidDataException(
                    $"Archive '{archive}' does not contain root probe '{probes[0].RelativePath}'.");
            }

            await context.Probes.VerifyAsync(
                source,
                componentId,
                probes,
                cancellationToken).ConfigureAwait(false);
            await ComponentCompletionStore.WriteAsync(
                source,
                componentId,
                fingerprint,
                probes,
                cancellationToken).ConfigureAwait(false);
            string prepared = source;
            if (!PathEquals(source, staging))
            {
                Directory.Move(source, ready);
                prepared = ready;
            }

            context.Paths.ReplaceDirectory(prepared, resolvedDestination);
            Console.WriteLine(
                $"[archive:ready] destination={resolvedDestination} "
                + $"probes={string.Join(",", probes.Select(probe => probe.RelativePath))}");
            return SetupPathGuard.RequireDirectory(resolvedDestination, "archive destination");
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                context.Paths.DeleteDirectory(staging);
            }

            if (Directory.Exists(ready))
            {
                context.Paths.DeleteDirectory(ready);
            }
        }
    }

    private async Task ExtractAsync(
        string archive,
        string destination,
        CancellationToken cancellationToken)
    {
        string lowerName = Path.GetFileName(archive).ToLowerInvariant();
        Console.WriteLine($"[archive:extract] archive={archive} staging={destination}");
        cancellationToken.ThrowIfCancellationRequested();
        if (lowerName.EndsWith(".zip", StringComparison.Ordinal))
        {
            await ZipArchiveExtractor.ExtractAsync(
                archive, destination, cancellationToken).ConfigureAwait(false);
        }
        else if (lowerName.EndsWith(".tar.gz", StringComparison.Ordinal)
            || lowerName.EndsWith(".tgz", StringComparison.Ordinal))
        {
            await using FileStream file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            await Task.Run(
                () => TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: false),
                cancellationToken).ConfigureAwait(false);
        }
        else if (lowerName.EndsWith(".tar", StringComparison.Ordinal))
        {
            await Task.Run(
                () => TarFile.ExtractToDirectory(archive, destination, overwriteFiles: false),
                cancellationToken).ConfigureAwait(false);
        }
        else if (lowerName.EndsWith(".tar.xz", StringComparison.Ordinal)
            || lowerName.EndsWith(".txz", StringComparison.Ordinal))
        {
            string tar = ResolveTar();
            await context.Commands.RunAsync(
                tar,
                ["-xf", archive, "-C", destination],
                new SetupCommandOptions(Timeout: TimeSpan.FromMinutes(20)),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException(
                $"Archive '{archive}' has an unsupported format. "
                + "Expected ZIP, tar, tar.gz, or tar.xz.");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<bool> IsReadyAsync(
        string root,
        string componentId,
        string fingerprint,
        IReadOnlyList<InstallationProbe> probes,
        CancellationToken cancellationToken)
    {
        if (!await ComponentCompletionStore.MatchesAsync(
            root,
            componentId,
            fingerprint,
            probes,
            cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await context.Probes.VerifyAsync(
                root,
                componentId,
                probes,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or SetupCommandException)
        {
            Console.Error.WriteLine(
                $"[archive:cache-invalid] destination={root} reason={exception.Message}");
            return false;
        }
    }

    private static string? LocateArchiveRoot(
        string staging,
        InstallationProbe firstProbe)
    {
        if (ProbeExists(staging, firstProbe))
        {
            return staging;
        }

        foreach (string directory in Directory.EnumerateDirectories(staging)
            .Order(StringComparer.Ordinal))
        {
            if (ProbeExists(directory, firstProbe))
            {
                return directory;
            }
        }

        return null;
    }

    private static bool ProbeExists(string root, InstallationProbe probe) =>
        InstallationProbeVerifier.Exists(root, probe);

    private static string ResolveTar()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ProgramLocator.RequireCommand(["tar"], "tar archive extractor");
        }

        string? windowsRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? Environment.GetEnvironmentVariable("WINDIR");
        if (string.IsNullOrWhiteSpace(windowsRoot))
        {
            throw new InvalidOperationException(
                "The Windows system directory could not be resolved for archive extraction.");
        }

        return SetupPathGuard.RequireFile(
            Path.Combine(windowsRoot, "System32", "tar.exe"),
            "Windows archive extractor");
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
