using System.Text.Json;
using Incant.Base;
using Incant.Core.Cpp;

namespace Incant.AutoTest.CppToolchain;

internal static class PreflightStage
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
        context.HostCapabilities =
            await InspectHostCapabilitiesAsync(
                context,
                cancellationToken).ConfigureAwait(false);
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

        if (manifest.Runner is null || manifest.Environment is null
            || manifest.Installations is null || manifest.Runtimes is null)
        {
            throw new AutoTestConfigurationException("The manifest contains null structural fields.");
        }

        if (manifest.Installations.Any(installation => installation is null
                || string.IsNullOrWhiteSpace(installation.Id) || installation.Environment is null)
            || manifest.Runtimes.Any(runtime => runtime is null || string.IsNullOrWhiteSpace(runtime.Id)))
        {
            throw new AutoTestConfigurationException("Every manifest entry must have an identity.");
        }

        if (manifest.Installations.Select(installation => installation.Id).Distinct().Count()
                != manifest.Installations.Count
            || manifest.Runtimes.Select(runtime => runtime.Id).Distinct().Count()
                != manifest.Runtimes.Count)
        {
            throw new AutoTestConfigurationException("Manifest entry ids must be unique.");
        }

        foreach (IReadOnlyDictionary<string, string?> environment in
            manifest.Installations.Select(installation => installation.Environment).Prepend(manifest.Environment))
        {
            if (environment.Keys.Any(name => string.IsNullOrWhiteSpace(name) || name.Contains('=')))
            {
                throw new AutoTestConfigurationException("The manifest contains an invalid environment variable name.");
            }
        }
    }

    private static async Task<HostExecutionCapabilities>
        InspectHostCapabilitiesAsync(
            AutoTestContext context,
            CancellationToken cancellationToken)
    {
        var architectures = new List<TargetArchitecture>
        {
            context.Profile.HostArchitecture,
        };
        if (context.Profile.HostOS != PlatformOS.OSX
            || context.Profile.HostArchitecture
                != TargetArchitecture.ARM64)
        {
            return new HostExecutionCapabilities(architectures);
        }

        try
        {
            ProcessResult result = await Misc.RunProcessAsync(
                "/usr/bin/arch",
                ["-x86_64", "/usr/bin/true"],
                new ProcessOptions
                {
                    Environment = context.BaseEnvironment,
                    Timeout = TimeSpan.FromSeconds(30),
                    EnsureUnixExecutablePermission = false,
                },
                cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                architectures.Add(TargetArchitecture.X64);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            // A failed translation probe means the architecture is unavailable.
        }

        return new HostExecutionCapabilities(architectures);
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
}
