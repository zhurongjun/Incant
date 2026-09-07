using Incant.Core.Cpp;
using SdkQuery = Incant.Core.Cpp.FindSdk.SdkQuery;
using ToolSet = Incant.Core.Cpp.FindTools.ToolSet;
using ToolSetQuery = Incant.Core.Cpp.FindTools.ToolSetQuery;

namespace Incant.AutoTest.CppToolchain.Shared;

internal enum InstallationKind
{
    VisualStudio,
    WindowsSdk,
    Gnu,
    Llvm,
    Xcode,
    AndroidNdk,
    Emscripten,
    WasiSdk,
}

internal enum RuntimeKind
{
    Node,
    Wasmtime,
    Python,
}

internal enum VersionSource
{
    Version,
    ProductVersion,
    CompilerVersion,
}

internal enum VersionPrecision
{
    Exact,
    Major,
    Minor,
}

internal sealed record VersionRule(
    string Value,
    VersionSource Source,
    VersionPrecision Precision)
{
    internal VersionConstraint Constraint
    {
        get
        {
            Version version = Parse(Value);
            return Precision switch
            {
                VersionPrecision.Exact => new VersionConstraint(exact: version),
                VersionPrecision.Major => new VersionConstraint(
                    minimumInclusive: new Version(version.Major, 0),
                    maximumExclusive: new Version(version.Major + 1, 0)),
                VersionPrecision.Minor => new VersionConstraint(
                    minimumInclusive: new Version(version.Major, version.Minor),
                    maximumExclusive: new Version(version.Major, version.Minor + 1)),
                _ => throw new ArgumentOutOfRangeException(nameof(Precision), Precision, null),
            };
        }
    }

    internal bool Matches(ToolSet toolSet)
    {
        Version? actual = Source switch
        {
            VersionSource.Version => toolSet.Version,
            VersionSource.ProductVersion => toolSet.ProductVersion,
            VersionSource.CompilerVersion => toolSet.CompilerVersion,
            _ => throw new ArgumentOutOfRangeException(nameof(Source), Source, null),
        };
        return Constraint.Matches(actual);
    }

    internal ToolSetQuery Apply(ToolSetQuery query) => Source switch
    {
        VersionSource.Version => query with { Version = Constraint },
        VersionSource.ProductVersion => query with { ProductVersion = Constraint },
        VersionSource.CompilerVersion => query with { CompilerVersion = Constraint },
        _ => throw new ArgumentOutOfRangeException(nameof(Source), Source, null),
    };

    internal SdkQuery Apply(SdkQuery query) => Source switch
    {
        VersionSource.Version or VersionSource.CompilerVersion => query with { Version = Constraint },
        VersionSource.ProductVersion => query with { ProductVersion = Constraint },
        _ => throw new ArgumentOutOfRangeException(nameof(Source), Source, null),
    };

    private static Version Parse(string value)
    {
        string normalized = value.Contains('.', StringComparison.Ordinal) ? value : value + ".0";
        return Version.Parse(normalized);
    }
}

internal sealed record InstallationRequirement(
    string Id,
    InstallationKind Kind,
    VersionRule? ToolVersion,
    VersionRule? SdkVersion,
    bool Required = true,
    ProvisioningMethod Provisioning = ProvisioningMethod.Default);

internal enum ProvisioningMethod
{
    Default,
    Linuxbrew,
}
