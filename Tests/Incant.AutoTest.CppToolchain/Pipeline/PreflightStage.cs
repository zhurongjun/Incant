using System.Text.Json;
using System.Text.RegularExpressions;
using Incant.Base;
using Incant.Core.Cpp;

namespace Incant.AutoTest.CppToolchain;

internal static partial class PreflightStage
{
    internal static async Task<bool> ExecuteAsync(
        AutoTestContext context,
        CancellationToken cancellationToken)
    {
        string manifestPath = context.Options.EnvironmentPath
            ?? throw new AutoTestConfigurationException(
                "--environment or INCANT_AUTOTEST_ENVIRONMENT must identify the prepared environment manifest.");
        if (!File.Exists(manifestPath))
        {
            throw new AutoTestConfigurationException(
                $"The environment manifest '{manifestPath}' does not exist.");
        }

        EnvironmentManifest manifest;
        try
        {
            manifest = await EnvironmentManifest.LoadAsync(
                manifestPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            throw new AutoTestConfigurationException(
                $"The environment manifest '{manifestPath}' could not be read: {exception.Message}",
                exception);
        }

        context.Manifest = manifest;
        ValidateManifest(context.Profile, manifest);
        ValidateHost(context.Profile);

        var baseEnvironment = new Dictionary<string, string?>(
            AutoTestContext.CaptureEnvironment(),
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        foreach ((string name, string? value) in manifest.Environment)
        {
            baseEnvironment[name] = value;
        }

        context.BaseEnvironment = baseEnvironment;
        AutoTestWorkspace.ResetRunDirectories(context);
        Directory.CreateDirectory(Path.GetDirectoryName(context.Options.ReportPath)!);

        foreach (string fixturePath in new[]
        {
            FixturePaths.Header,
            FixturePaths.StaticC,
            FixturePaths.StaticExtra,
            FixturePaths.StaticCpp,
            FixturePaths.SharedCpp,
            FixturePaths.MainC,
            FixturePaths.MainCpp,
        })
        {
            if (!File.Exists(fixturePath))
            {
                throw new AutoTestConfigurationException(
                    $"The signed-in fixture '{fixturePath}' was not copied to the application output.");
            }
        }

        await VerifyRequiredRuntimesAsync(
            context, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static void ValidateManifest(
        EnvironmentProfile profile,
        EnvironmentManifest manifest)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new AutoTestConfigurationException(
                $"Environment schema {manifest.SchemaVersion} is unsupported; expected schema 1.");
        }

        if (!string.Equals(manifest.Profile, profile.Name, StringComparison.Ordinal))
        {
            throw new AutoTestConfigurationException(
                $"Environment profile '{manifest.Profile}' does not match command '{profile.Name}'.");
        }

        if (manifest.Runner is null
            || string.IsNullOrWhiteSpace(manifest.Runner.ImageLabel)
            || string.IsNullOrWhiteSpace(manifest.Runner.ImageOS)
            || string.IsNullOrWhiteSpace(manifest.Runner.ImageVersion)
            || string.IsNullOrWhiteSpace(manifest.Runner.OS)
            || string.IsNullOrWhiteSpace(manifest.Runner.Architecture))
        {
            throw new AutoTestConfigurationException(
                "The environment manifest has incomplete runner metadata.");
        }

        if (!string.Equals(
            manifest.Runner.ImageLabel, profile.RunnerImage, StringComparison.Ordinal))
        {
            throw new AutoTestConfigurationException(
                $"Runner image '{manifest.Runner.ImageLabel}' does not match "
                + $"profile image '{profile.RunnerImage}'.");
        }

        string expectedOS = profile.HostOS switch
        {
            PlatformOS.Windows => "Windows",
            PlatformOS.Linux => "Linux",
            PlatformOS.OSX => "macOS",
            _ => throw new ArgumentOutOfRangeException(
                nameof(profile), profile.HostOS, null),
        };
        if (!string.Equals(
            manifest.Runner.OS, expectedOS, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                manifest.Runner.Architecture,
                profile.HostArchitecture.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new AutoTestConfigurationException(
                $"Manifest host '{manifest.Runner.OS}/{manifest.Runner.Architecture}' "
                + $"does not match profile host '{expectedOS}/{profile.HostArchitecture}'.");
        }

        if (manifest.Environment is null
            || manifest.Installations is null
            || manifest.Runtimes is null)
        {
            throw new AutoTestConfigurationException(
                "The environment manifest has null collection properties.");
        }

        string[] duplicateInstallationIds = manifest.Installations
            .GroupBy(installation => installation.Id, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateInstallationIds.Length > 0)
        {
            throw new AutoTestConfigurationException(
                "Environment installation ids must be unique: "
                + string.Join(", ", duplicateInstallationIds));
        }

        foreach (InstallationManifest installation in manifest.Installations)
        {
            if (string.IsNullOrWhiteSpace(installation.Id)
                || !Enum.IsDefined(installation.Kind)
                || string.IsNullOrWhiteSpace(installation.Version)
                || installation.Environment is null)
            {
                throw new AutoTestConfigurationException(
                    "An environment installation has incomplete identity metadata.");
            }

            ValidateExistingAbsolutePath(
                installation.RootPath,
                $"installation '{installation.Id}' root",
                expectFile: installation.Kind is InstallationKind.Gnu
                    or InstallationKind.Llvm);
            ValidateDeclaredVersion(
                installation.Version,
                $"installation '{installation.Id}' version");
            ValidateEnvironment(
                installation.Environment,
                $"installation '{installation.Id}'");
            bool requiresDownloadProvenance = installation.Kind is
                InstallationKind.AndroidNdk
                or InstallationKind.Emscripten
                or InstallationKind.WasiSdk;
            ValidateProvenance(
                $"installation '{installation.Id}'",
                installation.SourceUri,
                installation.Sha256,
                installation.Revision,
                requiresDownloadProvenance,
                requiresDownloadProvenance);
        }

        foreach (InstallationRequirement requirement in profile.Installations)
        {
            InstallationManifest? installation = manifest.Installations
                .SingleOrDefault(candidate => candidate.Id == requirement.Id);
            if (installation is null)
            {
                throw new AutoTestConfigurationException(
                    $"Required environment installation '{requirement.Id}' is absent.");
            }

            if (installation.Kind != requirement.Kind)
            {
                throw new AutoTestConfigurationException(
                    $"Installation '{requirement.Id}' is declared as "
                    + $"{installation.Kind}, expected {requirement.Kind}.");
            }
        }

        string[] duplicateRuntimeIds = manifest.Runtimes
            .GroupBy(runtime => runtime.Id, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateRuntimeIds.Length > 0)
        {
            throw new AutoTestConfigurationException(
                "Environment runtime ids must be unique: "
                + string.Join(", ", duplicateRuntimeIds));
        }

        foreach (RuntimeManifest runtime in manifest.Runtimes)
        {
            if (string.IsNullOrWhiteSpace(runtime.Id)
                || !Enum.IsDefined(runtime.Kind)
                || string.IsNullOrWhiteSpace(runtime.Version))
            {
                throw new AutoTestConfigurationException(
                    "An environment runtime has incomplete identity metadata.");
            }

            ValidateExistingAbsolutePath(
                runtime.Path, $"runtime '{runtime.Id}'", expectFile: true);
            ValidateDeclaredVersion(runtime.Version, $"runtime '{runtime.Id}' version");
            bool requiresDownloadProvenance = runtime.Kind is
                RuntimeKind.Node
                or RuntimeKind.Wasmtime
                || runtime.Kind == RuntimeKind.Python
                && profile.HostOS != PlatformOS.Linux;
            ValidateProvenance(
                $"runtime '{runtime.Id}'",
                runtime.SourceUri,
                runtime.Sha256,
                runtime.Revision,
                requiresDownloadProvenance,
                requireRevision: true);
            if (runtime.InstallationId is not null
                && !manifest.Installations.Any(
                    installation => installation.Id == runtime.InstallationId))
            {
                throw new AutoTestConfigurationException(
                    $"Runtime '{runtime.Id}' refers to unknown installation "
                    + $"'{runtime.InstallationId}'.");
            }
        }

        ValidateEnvironment(manifest.Environment, "base environment");
        foreach (InstallationRequirement requirement in profile.Installations
            .Where(requirement => requirement.Kind == InstallationKind.Emscripten))
        {
            _ = RequireRuntime(manifest, RuntimeKind.Node, requirement.Id);
            _ = RequireRuntime(manifest, RuntimeKind.Python, requirement.Id);
        }

        if (profile.Installations.Any(
            requirement => requirement.Kind == InstallationKind.WasiSdk))
        {
            _ = RequireRuntime(manifest, RuntimeKind.Wasmtime, null);
        }
    }

    private static async Task VerifyRequiredRuntimesAsync(
        AutoTestContext context,
        CancellationToken cancellationToken)
    {
        var required = new List<RuntimeManifest>();
        foreach (InstallationRequirement requirement in context.Profile.Installations
            .Where(requirement => requirement.Kind == InstallationKind.Emscripten))
        {
            required.Add(RequireRuntime(
                context.Manifest!, RuntimeKind.Node, requirement.Id));
            required.Add(RequireRuntime(
                context.Manifest!, RuntimeKind.Python, requirement.Id));
        }

        if (context.Profile.Installations.Any(
            requirement => requirement.Kind == InstallationKind.WasiSdk))
        {
            required.Add(RequireRuntime(
                context.Manifest!, RuntimeKind.Wasmtime, null));
        }

        foreach (RuntimeManifest runtime in required.DistinctBy(
            runtime => runtime.Id, StringComparer.Ordinal))
        {
            InstallationManifest? owner = runtime.InstallationId is null
                ? null
                : context.Manifest!.Installations.Single(
                    installation => installation.Id == runtime.InstallationId);
            ProcessResult result;
            try
            {
                result = await Misc.RunProcessAsync(
                    runtime.Path,
                    ["--version"],
                    new ProcessOptions
                    {
                        Environment = context.EnvironmentFor(owner),
                        Timeout = TimeSpan.FromSeconds(30),
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new AutoTestConfigurationException(
                    $"Runtime '{runtime.Id}' could not be executed: {exception.Message}",
                    exception);
            }

            if (!result.IsSuccess)
            {
                throw new AutoTestConfigurationException(
                    $"Runtime '{runtime.Id}' failed its version probe with exit code "
                    + $"{result.ExitCode?.ToString() ?? "timeout"}.");
            }

            Version? declared = ParseVersion(runtime.Version);
            Version? observed = ParseVersion(
                result.StandardOutput + result.StandardError);
            if (declared is null || observed is null || declared != observed)
            {
                throw new AutoTestConfigurationException(
                    $"Runtime '{runtime.Id}' reported version "
                    + $"'{observed?.ToString() ?? "unknown"}', expected '{runtime.Version}'.");
            }
        }
    }

    private static RuntimeManifest RequireRuntime(
        EnvironmentManifest manifest,
        RuntimeKind kind,
        string? installationId)
    {
        RuntimeManifest[] matches = manifest.Runtimes.Where(runtime =>
            runtime.Kind == kind
            && (installationId is null || runtime.InstallationId == installationId))
            .ToArray();
        if (matches.Length != 1)
        {
            string owner = installationId is null
                ? string.Empty
                : $" for '{installationId}'";
            throw new AutoTestConfigurationException(
                $"The environment manifest must contain one {kind} runtime{owner}; "
                + $"found {matches.Length}.");
        }

        return matches[0];
    }

    private static void ValidateEnvironment(
        IReadOnlyDictionary<string, string?> environment,
        string description)
    {
        if (environment.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new AutoTestConfigurationException(
                $"The {description} contains an empty environment variable name.");
        }
    }

    private static void ValidateExistingAbsolutePath(
        string path,
        string description,
        bool expectFile = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                throw new AutoTestConfigurationException(
                    $"The {description} must be an absolute path.");
            }

            bool exists = expectFile ? File.Exists(path) : Directory.Exists(path);
            if (!exists)
            {
                throw new AutoTestConfigurationException(
                    $"The {description} '{path}' does not exist.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new AutoTestConfigurationException(
                $"The {description} is invalid: {exception.Message}",
                exception);
        }
    }

    private static void ValidateDeclaredVersion(string value, string description)
    {
        string normalized = value.Contains('.', StringComparison.Ordinal)
            ? value
            : value + ".0";
        if (!Version.TryParse(normalized, out _))
        {
            throw new AutoTestConfigurationException(
                $"The {description} '{value}' is not a valid numeric version.");
        }
    }

    private static void ValidateProvenance(
        string description,
        string? sourceUri,
        string? sha256,
        string? revision,
        bool requireDownload,
        bool requireRevision)
    {
        bool hasSource = !string.IsNullOrWhiteSpace(sourceUri);
        bool hasSha256 = !string.IsNullOrWhiteSpace(sha256);
        if (hasSource != hasSha256)
        {
            throw new AutoTestConfigurationException(
                $"The {description} must declare its source URI and SHA-256 together.");
        }

        if (requireDownload && !hasSource)
        {
            throw new AutoTestConfigurationException(
                $"The {description} is downloaded but has no source URI and SHA-256.");
        }

        if (hasSource
            && (!Uri.TryCreate(sourceUri, UriKind.Absolute, out Uri? source)
                || !string.Equals(
                    source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AutoTestConfigurationException(
                $"The {description} source URI must be an absolute HTTPS URI.");
        }

        if (hasSha256 && !Sha256Pattern().IsMatch(sha256!))
        {
            throw new AutoTestConfigurationException(
                $"The {description} has an invalid SHA-256 value.");
        }

        if (requireRevision && string.IsNullOrWhiteSpace(revision))
        {
            throw new AutoTestConfigurationException(
                $"The {description} has no source revision.");
        }
    }

    private static Version? ParseVersion(string value)
    {
        if (Version.TryParse(value.Trim(), out Version? direct))
        {
            return direct;
        }

        Match match = VersionPattern().Match(value);
        return match.Success && Version.TryParse(
            match.Groups["version"].Value, out Version? reported)
            ? reported
            : null;
    }

    private static void ValidateHost(EnvironmentProfile profile)
    {
        if (Platform.OS != profile.HostOS)
        {
            throw new AutoTestConfigurationException(
                $"Profile '{profile.Name}' requires {profile.HostOS}; current host is {Platform.OS}.");
        }

        TargetArchitecture architecture = Platform.Arch switch
        {
            PlatformArch.X86 => TargetArchitecture.X86,
            PlatformArch.X64 => TargetArchitecture.X64,
            PlatformArch.ARM64 => TargetArchitecture.ARM64,
            _ => TargetArchitecture.Unknown,
        };
        if (architecture != profile.HostArchitecture)
        {
            throw new AutoTestConfigurationException(
                $"Profile '{profile.Name}' requires {profile.HostArchitecture}; "
                + $"current host is {architecture}.");
        }
    }

    [GeneratedRegex(
        @"(?im)(?:^v|(?:node|python|wasmtime)\s+v?)(?<version>\d+(?:\.\d+){1,3})",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
