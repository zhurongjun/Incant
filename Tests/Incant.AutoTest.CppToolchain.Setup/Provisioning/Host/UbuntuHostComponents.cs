namespace Incant.AutoTest.CppToolchain.Setup;

internal sealed class UbuntuPackagesComponent : ISetupComponent
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
        string[] packages =
        [
            "gcc-12", "g++-12", "gcc-12-multilib", "g++-12-multilib",
            "gcc-13", "g++-13", "gcc-13-multilib", "g++-13-multilib",
            "gcc-14", "g++-14", "gcc-14-multilib", "g++-14-multilib",
            "clang-16", "llvm-16", "lld-16",
            "clang-17", "llvm-17", "lld-17",
            "clang-18", "llvm-18", "lld-18",
            "unzip", "xz-utils",
        ];
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
