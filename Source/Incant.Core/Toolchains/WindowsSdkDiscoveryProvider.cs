using System.Runtime.Versioning;
using System.Security;
using Incant.Base;
using Microsoft.Win32;

namespace Incant.Core.Toolchains;

/// <summary>Discovers Windows platform SDK installations.</summary>
public sealed class WindowsSdkDiscoveryProvider : IDiscoveryProvider
{
    private static readonly IReadOnlyCollection<Kind> s_kinds = Array.AsReadOnly(
        [Kind.WindowsSdk, Kind.VisualStudio, Kind.Llvm]);

    /// <inheritdoc />
    public string Name => "Windows SDK";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds => s_kinds;

    /// <inheritdoc />
    public ValueTask<DiscoveryResult> DiscoverAsync(
        DiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(new DiscoveryResult());
        }

        var candidates = new List<Candidate>();
        var seenPaths = new HashSet<string>(ProviderUtilities.GetPathComparer());
        foreach (PathHint hint in context.GetExplicitPaths(Kind.WindowsSdk))
        {
            ProviderUtilities.AddCandidate(
                candidates,
                seenPaths,
                NormalizeKitRoot(hint.Path),
                Source.Explicit);
        }

        ProviderUtilities.AddCandidate(
            candidates,
            seenPaths,
            NormalizeKitRoot(context.GetEnvironmentVariable("WindowsSdkDir")),
            Source.Environment);
        AddRegistryCandidates(candidates, seenPaths);
        string? programFilesX86 = context.GetEnvironmentVariable("ProgramFiles(x86)");
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            ProviderUtilities.AddCandidate(
                candidates,
                seenPaths,
                Path.Combine(programFilesX86, "Windows Kits", "10"),
                Source.StandardPath);
        }

        var sdks = new List<SdkInstallation>();
        var diagnostics = new List<Diagnostic>();
        foreach (Candidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<SdkInstallation> installations = Inspect(candidate);
            if (installations.Count == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "invalid-candidate",
                    Name,
                    "The candidate does not contain a complete Windows SDK.",
                    candidate.Path));
            }
            else
            {
                sdks.AddRange(installations);
            }
        }

        return ValueTask.FromResult(new DiscoveryResult(sdks: sdks, diagnostics: diagnostics));
    }

    private static IReadOnlyList<SdkInstallation> Inspect(Candidate candidate)
    {
        string includeRoot = Path.Combine(candidate.Path, "Include");
        if (!Directory.Exists(includeRoot))
        {
            return [];
        }

        var installations = new List<SdkInstallation>();
        IEnumerable<string> versionDirectories;
        try
        {
            versionDirectories = Directory.EnumerateDirectories(includeRoot).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        foreach (string versionDirectory in versionDirectories)
        {
            string versionName = Path.GetFileName(versionDirectory);
            string windowsHeader = Path.Combine(versionDirectory, "um", "Windows.h");
            string ucrtHeader = Path.Combine(versionDirectory, "ucrt", "corecrt.h");
            string libraryRoot = Path.Combine(candidate.Path, "Lib", versionName);
            string binaryRoot = Path.Combine(candidate.Path, "bin", versionName);
            if (!File.Exists(windowsHeader)
                || !File.Exists(ucrtHeader)
                || !Directory.Exists(Path.Combine(libraryRoot, "um"))
                || !Directory.Exists(Path.Combine(libraryRoot, "ucrt"))
                || !ContainsSdkTools(binaryRoot))
            {
                continue;
            }

            var architectures = new List<TargetArchitecture>();
            AddArchitecture(architectures, libraryRoot, "x86", TargetArchitecture.X86);
            AddArchitecture(architectures, libraryRoot, "x64", TargetArchitecture.X64);
            AddArchitecture(architectures, libraryRoot, "arm64", TargetArchitecture.ARM64);
            if (architectures.Count == 0)
            {
                continue;
            }

            installations.Add(new SdkInstallation(
                Kind.WindowsSdk,
                TargetPlatform.Windows,
                candidate.Path,
                candidate.Path,
                ProviderUtilities.ParseVersion(versionName),
                candidate.Sources,
                architectures));
        }

        return installations;
    }

    private static void AddArchitecture(
        ICollection<TargetArchitecture> architectures,
        string libraryRoot,
        string directoryName,
        TargetArchitecture architecture)
    {
        if (File.Exists(Path.Combine(libraryRoot, "um", directoryName, "kernel32.lib"))
            && File.Exists(Path.Combine(libraryRoot, "ucrt", directoryName, "ucrt.lib")))
        {
            architectures.Add(architecture);
        }
    }

    private static bool ContainsSdkTools(string binaryRoot) =>
        new[] { "x64", "x86", "arm64" }.Any(architecture =>
            File.Exists(Path.Combine(binaryRoot, architecture, "rc.exe")));

    [SupportedOSPlatform("windows")]
    private static void AddRegistryCandidates(
        ICollection<Candidate> candidates,
        ISet<string> seenPaths)
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? key = localMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
                ProviderUtilities.AddCandidate(
                    candidates,
                    seenPaths,
                    key?.GetValue("KitsRoot10") as string,
                    Source.Vendor);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or SecurityException)
            {
            }
        }
    }

    private static string? NormalizeKitRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        DirectoryInfo? directory = new(fullPath);
        if (string.Equals(directory.Name, "Include", StringComparison.OrdinalIgnoreCase))
        {
            return directory.Parent?.FullName;
        }

        if (directory.Parent is not null
            && string.Equals(directory.Parent.Name, "Include", StringComparison.OrdinalIgnoreCase))
        {
            return directory.Parent.Parent?.FullName;
        }

        return fullPath;
    }
}
