namespace Incant.AutoTest.CXToolchain.Setup;

internal sealed class UbuntuPackagesComponent(IReadOnlyList<InstallationRequirement> requirements) : ISetupComponent
{
    public string Id => "ubuntu-packages";

    public string Name => "Ubuntu compiler packages";

    public IReadOnlyList<string> Dependencies => [];

    public async Task<ProvisioningResult> ProvisionAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        string sudo = ProgramLocator.RequireCommand(["sudo"], "sudo");
        var environment = new Dictionary<string, string?>
        {
            ["DEBIAN_FRONTEND"] = "noninteractive",
        };
        await context.Commands.RunAsync(
            sudo,
            ["apt-get", "update"],
            new SetupCommandOptions(
                Environment: environment,
                Timeout: TimeSpan.FromMinutes(20),
                Attempts: 3,
                RetryDelay: TimeSpan.FromSeconds(2)),
            cancellationToken).ConfigureAwait(false);
        string[] packages = requirements
            .Where(requirement => requirement.Provisioning == ProvisioningMethod.Default
                && requirement.Kind is InstallationKind.Gnu or InstallationKind.Llvm)
            .SelectMany(Packages).Concat(["unzip", "xz-utils"]).Distinct(StringComparer.Ordinal).ToArray();
        await context.Commands.RunAsync(
            sudo,
            ["apt-get", "install", "--yes", "--no-install-recommends", .. packages],
            new SetupCommandOptions(
                Environment: environment,
                Timeout: TimeSpan.FromMinutes(30),
                Attempts: 3,
                RetryDelay: TimeSpan.FromSeconds(2)),
            cancellationToken).ConfigureAwait(false);
        return ProvisioningResult.Empty;
    }

    private static IEnumerable<string> Packages(InstallationRequirement requirement)
    {
        int major = HostToolchainUtilities.Major(requirement);
        return requirement.Kind == InstallationKind.Gnu
            ? [$"gcc-{major}", $"g++-{major}", $"gcc-{major}-multilib", $"g++-{major}-multilib"]
            : [$"clang-{major}", $"llvm-{major}", $"lld-{major}"];
    }
}

internal sealed class CompilerInventoryComponent(
    InstallationRequirement requirement,
    IReadOnlyList<string> cCandidates,
    IReadOnlyList<string> cxxCandidates,
    IReadOnlyList<string> dependencies)
    : ISetupComponent
{
    public string Id => requirement.Id;

    public string Name => $"{requirement.Id} inventory";

    public IReadOnlyList<string> Dependencies => dependencies;

    public async Task<ProvisioningResult> ProvisionAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        int major = HostToolchainUtilities.Major(requirement);
        LocatedProgram compiler = await ProgramLocator.ResolveCompilerAsync(
            context,
            cCandidates,
            major,
            requirement.Id,
            cancellationToken).ConfigureAwait(false);
        LocatedProgram cxxCompiler = await ProgramLocator.ResolveCompilerAsync(
            context,
            cxxCandidates,
            major,
            $"{requirement.Id} C++ compiler",
            cancellationToken).ConfigureAwait(false);
        return ProvisioningResult.ForInstallation(new InstallationManifest
        {
            Id = requirement.Id,
            Kind = requirement.Kind,
            RootPath = compiler.Path,
            Version = compiler.Version,
            Environment = new Dictionary<string, string?>
            {
                ["CC"] = compiler.Path,
                ["CXX"] = cxxCompiler.Path,
            },
            SourceUri = null,
            Sha256 = null,
            Revision = null,
        });
    }
}
