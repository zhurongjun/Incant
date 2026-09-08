using System.Security.Cryptography;
using System.Text;
using Sdk = Incant.CX.FindSdk.Sdk;
using ToolSet = Incant.CX.FindTools.ToolSet;

namespace Incant.AutoTest.CXToolchain;

internal static class InstallationIdentity
{
    private static string PathKey(string path)
    {
        string normalized = PathIdentity.Normalize(path);
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    internal static string ToolSetKey(ToolSet toolSet) =>
        string.Join("|", toolSet.Kind, PathKey(toolSet.RootPath),
            PathKey(toolSet.EnvironmentPath), toolSet.Version,
            toolSet.CompilerPath is null ? null : PathKey(toolSet.CompilerPath),
            toolSet.DefaultTargetTriple);

    internal static string SdkKey(Sdk sdk) =>
        string.Join("|", sdk.Kind, PathKey(sdk.RootPath),
            PathKey(sdk.EnvironmentPath), sdk.Version,
            sdk.CompilerPath is null ? null : PathKey(sdk.CompilerPath));

    internal static string ShortId(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    internal static bool Contains(InstallationManifest manifest, ToolSet toolSet) =>
        File.Exists(manifest.RootPath)
            ? toolSet.CompilerPath is not null
                && PathIdentity.AreEqual(manifest.RootPath, toolSet.CompilerPath)
            : PathIdentity.Contains(manifest.RootPath, toolSet.RootPath);

    internal static bool Contains(InstallationManifest manifest, Sdk sdk) =>
        File.Exists(manifest.RootPath)
            ? sdk.CompilerPath is not null
                && PathIdentity.AreEqual(manifest.RootPath, sdk.CompilerPath)
            : PathIdentity.Contains(manifest.RootPath, sdk.RootPath);

    internal static InstallationManifest Ambient(string id, InstallationKind kind, string root, string version,
        string? developerDirectory = null) => new()
        {
            Id = id,
            Kind = kind,
            RootPath = root,
            Version = version,
            Environment = developerDirectory is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["DEVELOPER_DIR"] = developerDirectory },
            SourceUri = null,
            Sha256 = null,
            Revision = null,
        };
}
