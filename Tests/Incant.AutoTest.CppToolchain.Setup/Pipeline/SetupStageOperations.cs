using System.Runtime.InteropServices;
using Incant.Base;
using Incant.Core.Cpp;

namespace Incant.AutoTest.CppToolchain.Setup;

internal static class SetupStageOperations
{
    internal static Task PreflightAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CurrentOS() != context.Profile.HostOS)
        {
            throw new SetupConfigurationException(
                $"Profile '{context.Profile.Name}' requires {context.Profile.HostOS}; "
                + $"current host is {CurrentOS()}.");
        }

        if (CurrentArchitecture() != context.Profile.HostArchitecture)
        {
            throw new SetupConfigurationException(
                $"Profile '{context.Profile.Name}' requires {context.Profile.HostArchitecture}; "
                + $"current host is {CurrentArchitecture()}.");
        }

        _ = SetupPathGuard.RequireDirectory(context.Options.Workspace, "workspace");
        _ = SetupPathGuard.RequireFile(
            Path.Combine(
                context.Options.Workspace,
                "Tests",
                "Incant.AutoTest.CppToolchain",
                "Incant.AutoTest.CppToolchain.csproj"),
            "AutoTest project");
        _ = SetupPathGuard.RequireFile(
            Path.Combine(
                context.Options.Workspace,
                "Tests",
                "Incant.AutoTest.CppToolchain.Setup",
                "Incant.AutoTest.CppToolchain.Setup.csproj"),
            "Setup project");
        _ = ProgramLocator.RequireCommand(["dotnet"], ".NET SDK");

        Directory.CreateDirectory(context.Options.ToolchainRoot);
        Directory.CreateDirectory(context.Paths.DownloadsRoot);
        Directory.CreateDirectory(context.Options.WorkRoot);
        Directory.CreateDirectory(
            Path.GetDirectoryName(context.Options.EnvironmentPath)
                ?? throw new SetupConfigurationException(
                    "The environment manifest path has no parent directory."));
        Directory.CreateDirectory(
            Path.GetDirectoryName(context.Options.ReportPath)
                ?? throw new SetupConfigurationException(
                    "The setup report path has no parent directory."));
        File.Delete(context.Options.EnvironmentPath);
        File.Delete(context.PendingManifestPath);

        Console.WriteLine(
            $"[setup] dotnet={RuntimeInformation.FrameworkDescription} "
            + $"os={RuntimeInformation.OSDescription} architecture={RuntimeInformation.OSArchitecture}");
        Console.WriteLine($"[setup] workspace={context.Options.Workspace}");
        Console.WriteLine($"[setup] toolchainRoot={context.Options.ToolchainRoot}");
        Console.WriteLine($"[setup] manifest={context.Options.EnvironmentPath}");
        Console.WriteLine($"[setup] report={context.Options.ReportPath}");
        return Task.CompletedTask;
    }

    internal static async Task PrepareManifestAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        EnvironmentManifest manifest = context.CreateManifest();
        await EnvironmentManifest.SaveAsync(
            context.PendingManifestPath,
            manifest,
            cancellationToken).ConfigureAwait(false);
        context.ManifestPrepared = true;
        Console.WriteLine(
            $"[manifest:prepared] installations={manifest.Installations.Count} "
            + $"runtimes={manifest.Runtimes.Count} path={context.PendingManifestPath}");
    }

    internal static async Task BuildAutoTestAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        string project = Path.Combine(
            context.Options.Workspace,
            "Tests",
            "Incant.AutoTest.CppToolchain",
            "Incant.AutoTest.CppToolchain.csproj");
        string dotnet = ProgramLocator.RequireCommand(["dotnet"], ".NET SDK");
        await context.Commands.RunAsync(
            dotnet,
            ["restore", project],
            new SetupCommandOptions(
                WorkingDirectory: context.Options.Workspace,
                Timeout: TimeSpan.FromMinutes(15)),
            cancellationToken).ConfigureAwait(false);
        await context.Commands.RunAsync(
            dotnet,
            ["build", project, "--configuration", "Release", "--no-restore"],
            new SetupCommandOptions(
                WorkingDirectory: context.Options.Workspace,
                Timeout: TimeSpan.FromMinutes(20)),
            cancellationToken).ConfigureAwait(false);
        context.BuildSucceeded = true;
    }

    internal static async Task ExportEnvironmentAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        string appHostDirectory = SetupPathGuard.RequireDirectory(
            Path.Combine(
                context.Options.Workspace,
                "build",
                "bin",
                "Incant.AutoTest.CppToolchain",
                "release"),
            "AutoTest apphost directory");
        _ = SetupPathGuard.RequireFile(
            Path.Combine(
                appHostDirectory,
                OperatingSystem.IsWindows()
                    ? "Incant.AutoTest.CppToolchain.exe"
                    : "Incant.AutoTest.CppToolchain"),
            "AutoTest apphost");

        string manifestPath = Path.GetFullPath(context.Options.EnvironmentPath);
        string? githubPath = Environment.GetEnvironmentVariable("GITHUB_PATH");
        if (!string.IsNullOrWhiteSpace(githubPath))
        {
            await File.AppendAllTextAsync(
                githubPath,
                SingleLine(appHostDirectory) + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }

        string? githubEnvironment = Environment.GetEnvironmentVariable("GITHUB_ENV");
        if (!string.IsNullOrWhiteSpace(githubEnvironment))
        {
            await File.AppendAllTextAsync(
                githubEnvironment,
                $"INCANT_AUTOTEST_ENVIRONMENT={SingleLine(manifestPath)}{Environment.NewLine}",
                cancellationToken).ConfigureAwait(false);
        }

        EnvironmentManifest manifest = context.CreateManifest();
        await EnvironmentManifest.SaveAsync(
            manifestPath,
            manifest,
            cancellationToken).ConfigureAwait(false);
        context.EnvironmentExported = true;
        File.Delete(context.PendingManifestPath);
        Console.WriteLine($"[environment:export] apphostDirectory={appHostDirectory}");
        Console.WriteLine($"[environment:export] INCANT_AUTOTEST_ENVIRONMENT={manifestPath}");
    }

    private static PlatformOS CurrentOS()
    {
        if (OperatingSystem.IsWindows())
        {
            return PlatformOS.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            return PlatformOS.Linux;
        }

        if (OperatingSystem.IsMacOS())
        {
            return PlatformOS.OSX;
        }

        return PlatformOS.Unknown;
    }

    private static TargetArchitecture CurrentArchitecture() =>
        RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => TargetArchitecture.X64,
            Architecture.Arm64 => TargetArchitecture.ARM64,
            Architecture.X86 => TargetArchitecture.X86,
            Architecture.Arm => TargetArchitecture.ARM,
            _ => TargetArchitecture.Unknown,
        };

    private static string SingleLine(string value)
    {
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new InvalidDataException(
                "GitHub environment file values must not contain line breaks.");
        }

        return value;
    }
}
