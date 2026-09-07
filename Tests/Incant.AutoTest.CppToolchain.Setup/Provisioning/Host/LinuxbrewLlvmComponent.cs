using System.Globalization;
using System.Text.Json;

namespace Incant.AutoTest.CppToolchain.Setup;

/// <summary>Installs and inventories the versioned Linuxbrew LLVM keg through Homebrew.</summary>
internal sealed class LinuxbrewLlvmComponent(
    InstallationRequirement requirement)
    : ISetupComponent
{
    private const string Formula = "llvm@18";

    public string Id => requirement.Id;

    public string Name => "Linuxbrew LLVM 18";

    public IReadOnlyList<string> Dependencies => [];

    public async Task<ProvisioningResult> ProvisionAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        string brew = ProgramLocator.RequireCommand(
            BrewCandidates(),
            "Linuxbrew");
        IReadOnlyDictionary<string, string?> brewEnvironment =
            BrewEnvironment();
        await context.Commands.RunAsync(
            brew,
            ["install", "--force-bottle", Formula],
            new SetupCommandOptions(
                Environment: brewEnvironment,
                Timeout: TimeSpan.FromHours(1),
                Attempts: 3,
                RetryDelay: TimeSpan.FromSeconds(5)),
            cancellationToken).ConfigureAwait(false);

        SetupCommandOutput prefixOutput =
            await context.Commands.RunAsync(
                brew,
                ["--prefix"],
                new SetupCommandOptions(
                    Environment: brewEnvironment,
                    Timeout: TimeSpan.FromMinutes(5)),
                cancellationToken).ConfigureAwait(false);
        string prefix = FirstOutputLine(
            prefixOutput.StandardOutput,
            "Homebrew prefix");
        if (!Path.IsPathFullyQualified(prefix))
        {
            throw new InvalidDataException(
                $"Homebrew reported a non-absolute prefix '{prefix}'.");
        }

        string root = Path.Combine(
            Path.GetFullPath(prefix),
            "opt",
            Formula);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"The stable Homebrew opt entry '{root}' does not exist.");
        }

        string bin = Path.Combine(root, "bin");
        string clang = RequireInvocationFile(
            Path.Combine(bin, "clang-18"),
            "Linuxbrew clang-18");
        string clangxx = RequireInvocationFile(
            Path.Combine(bin, "clang++"),
            "Linuxbrew clang++");
        string llvmAr = RequireInvocationFile(
            Path.Combine(bin, "llvm-ar"),
            "Linuxbrew llvm-ar");
        string llvmRanlib = RequireInvocationFile(
            Path.Combine(bin, "llvm-ranlib"),
            "Linuxbrew llvm-ranlib");
        string linker = RequireInvocationFile(
            Path.Combine(bin, "ld.lld"),
            "Linuxbrew ld.lld");

        string version = await ProgramLocator.GetVersionAsync(
            context,
            clang,
            "clang",
            cancellationToken).ConfigureAwait(false);
        string expectedVersion = requirement.ToolVersion?.Value
            ?? throw new SetupConfigurationException(
                $"Requirement '{requirement.Id}' has no compiler version.");
        if (ProgramLocator.CompareVersions(
            version,
            expectedVersion) != 0)
        {
            throw new InvalidDataException(
                $"'{clang}' reports version {version}; "
                + $"expected {expectedVersion}.");
        }

        foreach ((string path, string identity) in new[]
        {
            (clangxx, "clang"),
            (llvmAr, "generic"),
            (llvmRanlib, "generic"),
            (linker, "generic"),
        })
        {
            string toolVersion =
                await ProgramLocator.GetVersionAsync(
                    context,
                    path,
                    identity,
                    cancellationToken).ConfigureAwait(false);
            HostToolchainUtilities.RequireMajor(
                path,
                toolVersion,
                18);
        }

        SetupCommandOutput information =
            await context.Commands.RunAsync(
                brew,
                ["info", "--json=v2", Formula],
                new SetupCommandOptions(
                    Environment: brewEnvironment,
                    Timeout: TimeSpan.FromMinutes(5)),
                cancellationToken).ConfigureAwait(false);
        string revision = FormulaRevision(
            information.StandardOutput);
        return ProvisioningResult.ForInstallation(
            new InstallationManifest
            {
                Id = requirement.Id,
                Kind = InstallationKind.Llvm,
                RootPath = clang,
                Version = version,
                Environment =
                    new Dictionary<string, string?>
                    {
                        ["CC"] = clang,
                        ["CXX"] = clangxx,
                        ["LLVM_PATH"] = bin,
                        ["PATH"] =
                            HostToolchainUtilities.PrependPath(bin),
                    },
                SourceUri = null,
                Sha256 = null,
                Revision = revision,
            });
    }

    private static IReadOnlyList<string> BrewCandidates()
    {
        var candidates = new List<string>();
        string? environmentPrefix =
            Environment.GetEnvironmentVariable(
                "HOMEBREW_PREFIX");
        if (!string.IsNullOrWhiteSpace(environmentPrefix))
        {
            candidates.Add(Path.Combine(
                environmentPrefix,
                "bin",
                "brew"));
        }

        candidates.AddRange(
        [
            "/home/linuxbrew/.linuxbrew/bin/brew",
            "/opt/homebrew/bin/brew",
            "/usr/local/bin/brew",
            "brew",
        ]);
        return candidates;
    }

    private static IReadOnlyDictionary<string, string?>
        BrewEnvironment() => new Dictionary<string, string?>
        {
            ["HOMEBREW_NO_AUTO_UPDATE"] = "1",
            ["HOMEBREW_NO_ANALYTICS"] = "1",
            ["HOMEBREW_NO_INSTALL_CLEANUP"] = "1",
            ["HOMEBREW_NO_ENV_HINTS"] = "1",
        };

    private static string RequireInvocationFile(
        string path,
        string description)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"{description} was not found at '{fullPath}'.",
                fullPath);
        }

        return fullPath;
    }

    private static string FirstOutputLine(
        string output,
        string description)
    {
        string? value = output.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException(
                $"{description} command returned no output.");
    }

    private static string FormulaRevision(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement formulae =
            document.RootElement.GetProperty("formulae");
        if (formulae.GetArrayLength() != 1)
        {
            throw new InvalidDataException(
                $"Homebrew returned {formulae.GetArrayLength()} "
                + $"formula records for '{Formula}'.");
        }

        JsonElement formula = formulae[0];
        int revision = formula.TryGetProperty(
            "revision",
            out JsonElement revisionValue)
            && revisionValue.TryGetInt32(out int parsedRevision)
                ? parsedRevision
                : 0;
        string stableVersion = formula.TryGetProperty(
            "versions",
            out JsonElement versions)
            && versions.TryGetProperty(
                "stable",
                out JsonElement stable)
            ? stable.GetString() ?? "unknown"
            : "unknown";
        int bottleRebuild = formula.TryGetProperty(
            "bottle",
            out JsonElement bottle)
            && bottle.TryGetProperty(
                "stable",
                out JsonElement stableBottle)
            && stableBottle.TryGetProperty(
                "rebuild",
                out JsonElement rebuild)
            && rebuild.TryGetInt32(out int parsedRebuild)
                ? parsedRebuild
                : 0;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"formula={Formula};version={stableVersion};"
            + $"revision={revision};bottleRebuild={bottleRebuild}");
    }
}
