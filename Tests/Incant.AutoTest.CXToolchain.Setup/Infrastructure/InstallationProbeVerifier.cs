namespace Incant.AutoTest.CXToolchain.Setup;

internal enum ProbeKind
{
    File,
    Directory,
    Executable,
}

internal sealed record InstallationProbe(
    string RelativePath,
    ProbeKind Kind,
    IReadOnlyList<string>? Arguments = null);

internal sealed class InstallationProbeVerifier(SetupCommandRunner commands)
{
    internal async Task VerifyAsync(
        string root,
        string componentId,
        IReadOnlyList<InstallationProbe> probes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        ArgumentNullException.ThrowIfNull(probes);
        if (probes.Count == 0)
        {
            throw new ArgumentException(
                "At least one installation probe is required.",
                nameof(probes));
        }

        string resolvedRoot = Path.GetFullPath(root);
        foreach (InstallationProbe probe in probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateArguments(probe);
            string path = ResolvePath(resolvedRoot, probe.RelativePath);
            switch (probe.Kind)
            {
                case ProbeKind.File:
                    Require(File.Exists(path), componentId, probe, path);
                    break;
                case ProbeKind.Directory:
                    Require(Directory.Exists(path), componentId, probe, path);
                    break;
                case ProbeKind.Executable:
                    Require(File.Exists(path), componentId, probe, path);
                    await commands.RunAsync(
                        path,
                        probe.Arguments ?? [],
                        new SetupCommandOptions(
                            WorkingDirectory: resolvedRoot,
                            Timeout: TimeSpan.FromMinutes(2)),
                        cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(probes), probe.Kind, null);
            }
        }
    }

    internal static bool Exists(string root, InstallationProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        string path = ResolvePath(root, probe.RelativePath);
        return probe.Kind == ProbeKind.Directory
            ? Directory.Exists(path)
            : File.Exists(path);
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
            || relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            || relative.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Probe path '{path}' must remain inside the installation root.",
                nameof(path));
        }

        return relative;
    }

    private static string ResolvePath(string root, string relativePath) =>
        Path.Combine(root, NormalizeRelativePath(relativePath));

    private static void ValidateArguments(InstallationProbe probe)
    {
        if (probe.Kind != ProbeKind.Executable && probe.Arguments is { Count: > 0 })
        {
            throw new ArgumentException(
                $"Only executable probe '{probe.RelativePath}' may declare arguments.",
                nameof(probe));
        }
    }

    private static void Require(
        bool condition,
        string componentId,
        InstallationProbe probe,
        string path)
    {
        if (!condition)
        {
            throw new InvalidDataException(
                $"Component '{componentId}' failed {probe.Kind} probe "
                + $"'{probe.RelativePath}' at '{path}'.");
        }
    }
}
