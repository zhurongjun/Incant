using System.Text.Json;

namespace Incant.AutoTest.CppToolchain.Setup;

internal sealed class VisualStudioInventoryComponent(string id, int productMajor)
    : ISetupComponent
{
    public string Id => id;

    public string Name => $"Visual Studio {productMajor} inventory";

    public IReadOnlyList<string> Dependencies => [];

    public async Task<ProvisioningResult> ProvisionAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        string programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)")
            ?? throw new InvalidOperationException(
                "The ProgramFiles(x86) environment variable is missing.");
        string vswhere = SetupPathGuard.RequireFile(
            Path.Combine(
                programFilesX86,
                "Microsoft Visual Studio",
                "Installer",
                "vswhere.exe"),
            "vswhere");
        SetupCommandOutput output = await context.Commands.RunAsync(
            vswhere,
            ["-all", "-prerelease", "-products", "*", "-format", "json", "-utf8"],
            new SetupCommandOptions(Timeout: TimeSpan.FromMinutes(5)),
            cancellationToken).ConfigureAwait(false);

        VisualStudioInstance[] instances = ParseInstances(output.StandardOutput);
        VisualStudioInstance? selected = instances
            .Where(instance => instance.Path is not null
                && instance.Version is not null
                && ProgramLocator.VersionMajor(instance.Version) == productMajor)
            .OrderByDescending(
                instance => instance.Version,
                Comparer<string?>.Create((left, right) =>
                    ProgramLocator.CompareVersions(left ?? string.Empty, right ?? string.Empty)))
            .FirstOrDefault();
        if (selected?.Path is null || selected.Version is null)
        {
            string discovered = string.Join(
                "; ",
                instances.Select(instance =>
                    $"{instance.Path ?? "unknown"} ({instance.Version ?? "unknown"})"));
            throw new FileNotFoundException(
                $"Visual Studio product major {productMajor} was not found. "
                + $"Discovered: {(discovered.Length == 0 ? "none" : discovered)}.");
        }

        string root = SetupPathGuard.RequireDirectory(
            selected.Path,
            $"Visual Studio {productMajor}");
        _ = SetupPathGuard.RequireDirectory(
            Path.Combine(root, "VC", "Tools", "MSVC"),
            "MSVC toolsets");
        return ProvisioningResult.ForInstallation(new InstallationManifest
        {
            Id = id,
            Kind = InstallationKind.VisualStudio,
            RootPath = root,
            Version = selected.Version,
            Environment = new Dictionary<string, string?>(),
            SourceUri = null,
            Sha256 = null,
            Revision = null,
        });
    }

    private static VisualStudioInstance[] ParseInstances(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json.TrimStart('\uFEFF'));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException(
                    "vswhere did not return an array of Visual Studio instances.");
            }

            return document.RootElement.EnumerateArray()
                .Select(element => new VisualStudioInstance(
                    element.TryGetProperty("installationPath", out JsonElement path)
                        ? path.GetString()
                        : null,
                    element.TryGetProperty("installationVersion", out JsonElement version)
                        ? version.GetString()
                        : null))
                .ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"vswhere returned invalid JSON: {json[..Math.Min(2000, json.Length)]}",
                exception);
        }
    }

    private sealed record VisualStudioInstance(string? Path, string? Version);
}

internal sealed class WindowsSdkInventoryComponent(string id, string version)
    : ISetupComponent
{
    public string Id => id;

    public string Name => $"Windows SDK {version} inventory";

    public IReadOnlyList<string> Dependencies => [];

    public Task<ProvisioningResult> ProvisionAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)")
            ?? throw new InvalidOperationException(
                "The ProgramFiles(x86) environment variable is missing.");
        string kitRoot = Path.Combine(programFilesX86, "Windows Kits", "10");
        string includeRoot = SetupPathGuard.RequireDirectory(
            Path.Combine(kitRoot, "Include", version),
            $"Windows SDK {version} include root");
        _ = SetupPathGuard.RequireDirectory(
            Path.Combine(kitRoot, "Lib", version),
            $"Windows SDK {version} library root");
        return Task.FromResult(ProvisioningResult.ForInstallation(
            new InstallationManifest
            {
                Id = id,
                Kind = InstallationKind.WindowsSdk,
                RootPath = includeRoot,
                Version = version,
                Environment = new Dictionary<string, string?>(),
                SourceUri = null,
                Sha256 = null,
                Revision = null,
            }));
    }
}

internal sealed class WindowsLlvmInventoryComponent(string id, int major)
    : ISetupComponent
{
    public string Id => id;

    public string Name => $"LLVM {major} inventory";

    public IReadOnlyList<string> Dependencies => [];

    public async Task<ProvisioningResult> ProvisionAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        LocatedProgram clang = await ProgramLocator.ResolveCompilerAsync(
            context,
            [
                $"clang-{major}.exe",
                "clang.exe",
                @"C:\Program Files\LLVM\bin\clang.exe",
                $@"C:\Program Files\LLVM-{major}\bin\clang.exe",
            ],
            major,
            "LLVM",
            cancellationToken).ConfigureAwait(false);
        string directory = Path.GetDirectoryName(clang.Path)
            ?? throw new InvalidDataException($"LLVM path '{clang.Path}' has no directory.");
        string clangxx = SetupPathGuard.RequireFile(
            Path.Combine(directory, "clang++.exe"),
            $"clang++ {major}");
        string clangxxVersion = await ProgramLocator.GetVersionAsync(
            context, clangxx, "clang", cancellationToken).ConfigureAwait(false);
        HostToolchainUtilities.RequireMajor(clangxx, clangxxVersion, major);
        return ProvisioningResult.ForInstallation(new InstallationManifest
        {
            Id = id,
            Kind = InstallationKind.Llvm,
            RootPath = clang.Path,
            Version = clang.Version,
            Environment = new Dictionary<string, string?>
            {
                ["CC"] = clang.Path,
                ["CXX"] = clangxx,
                ["PATH"] = HostToolchainUtilities.PrependPath(directory),
            },
            SourceUri = null,
            Sha256 = null,
            Revision = null,
        });
    }
}
