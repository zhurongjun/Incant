using Incant.Core.Cpp;

namespace Incant.Core.Cpp.FindTools;

/// <summary>A probed GNU or LLVM compiler installation with role-aware companion lookup.</summary>
internal sealed class CompilerToolSet : ToolSet
{
    private readonly CompilerInstallation _installation;

    private readonly DiscoveryContext _context;

    internal CompilerToolSet(
        CompilerInstallation installation,
        DiscoveryContext context)
        : base(
            installation.Family == CompilerFamily.Gnu
                ? Kind.Gnu
                : Kind.Llvm,
            installation.BinPath,
            installation.EnvironmentPath,
            installation.Version,
            installation.Version,
            installation.Version,
            installation.InvocationPath,
            installation.DefaultTargetTriple,
            installation.Channel,
            installation.Sources)
    {
        _installation = installation;
        _context = context;
    }

    protected override Task<Tool?> FindToolCoreAsync(
        string name,
        ToolQuery query,
        CancellationToken cancellationToken) =>
        ToolRoleResolver.FindAsync(
            _installation,
            name,
            query,
            _context,
            cancellationToken);
}
