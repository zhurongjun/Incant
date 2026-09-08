using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Incant.CX;
using SdkCompilerProvider = Incant.CX.FindSdk.CompilerProvider;
using SdkFinder = Incant.CX.FindSdk.Finder;
using SdkKind = Incant.CX.FindSdk.Kind;
using SdkQuery = Incant.CX.FindSdk.SdkQuery;
using Tool = Incant.CX.FindTools.Tool;
using ToolCompilerProvider = Incant.CX.FindTools.CompilerProvider;
using ToolFinder = Incant.CX.FindTools.Finder;
using ToolKind = Incant.CX.FindTools.Kind;
using ToolNames = Incant.CX.FindTools.ToolNames;
using ToolQuery = Incant.CX.FindTools.ToolQuery;
using ToolSet = Incant.CX.FindTools.ToolSet;

namespace Incant.UnitTest.CX.FindTools;

public sealed class CompilerProviderTests
{
    [Fact]
    public async Task HomebrewOptEntryPreservesInvocationAndFindsUnversionedTools()
    {
        RequireUnix();
        using var installation = new SyntheticInstallation();
        string cellar = installation.Directory("Cellar/llvm@18/18.1.8");
        installation.Directory("Cellar/llvm@18/18.1.8/bin");
        installation.Compiler("Cellar/llvm@18/18.1.8/bin/clang-18", CompilerIdentity.Llvm, "18.1.8");
        installation.Compiler("Cellar/llvm@18/18.1.8/bin/clang++", CompilerIdentity.Llvm, "18.1.8");
        installation.VersionTool("Cellar/llvm@18/18.1.8/bin/llvm-ar", "LLVM version 18.1.8");
        installation.VersionTool("Cellar/llvm@18/18.1.8/bin/llvm-ranlib", "LLVM version 18.1.8");
        installation.VersionTool("Cellar/llvm@18/18.1.8/bin/ld.lld", "LLD 18.1.8");
        string opt = installation.Directory("opt");
        Directory.CreateSymbolicLink(Path.Combine(opt, "llvm@18"), cellar);
        string compilerPath = Path.Combine(opt, "llvm@18", "bin", "clang-18");

        ToolSet? discovered = await new ToolFinder(
        [
            new ToolCompilerProvider(),
        ]).FindToolSetAsync(
            new Incant.CX.FindTools.ToolSetQuery
            {
                Kind = ToolKind.Llvm,
                CompilerVersion = new VersionConstraint(
                    exact: new Version(18, 1, 8)),
                IncludePreview = true,
                Environment = new Dictionary<string, string?>(Environment())
                {
                    ["HOMEBREW_PREFIX"] = installation.RootPath,
                },
            },
            TestContext.Current.CancellationToken);
        ToolSet toolSet = Assert.IsAssignableFrom<ToolSet>(discovered);
        Tool? cppCompiler = await toolSet.FindToolAsync(
            ToolNames.Clangxx, cancellationToken: TestContext.Current.CancellationToken);
        Tool? archiver = await toolSet.FindToolAsync(
            ToolNames.LlvmAr, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(compilerPath, toolSet.CompilerPath);
        Assert.Equal(Path.Combine(opt, "llvm@18", "bin", "clang++"), cppCompiler?.Path);
        Assert.Equal(Path.Combine(opt, "llvm@18", "bin", "llvm-ar"), archiver?.Path);
        Assert.Equal(Path.Combine(opt, "llvm@18", "bin"), toolSet.RootPath);
    }

    [Fact]
    public async Task SharedVersionedToolsAndPrivateSlotToolsRemainUsable()
    {
        RequireUnix();
        using var installation = new SyntheticInstallation();
        string shared = installation.Directory("usr/bin");
        string slot = installation.Directory("usr/lib/llvm18/bin");
        string clang = installation.Compiler("usr/lib/llvm18/bin/clang", CompilerIdentity.Llvm, "18.1.8");
        string clangxx = installation.Compiler("usr/lib/llvm18/bin/clang++", CompilerIdentity.Llvm, "18.1.8");
        string privateArchiver = installation.VersionTool(
            "usr/lib/llvm18/bin/llvm-ar", "LLVM version 18.1.8");
        File.CreateSymbolicLink(Path.Combine(shared, "clang-18"), clang);
        File.CreateSymbolicLink(Path.Combine(shared, "clang++-18"), clangxx);
        string sharedArchiver = Path.Combine(shared, "llvm-ar-18");
        File.CreateSymbolicLink(sharedArchiver, privateArchiver);

        ToolSet toolSet = await FindToolSetAsync(shared, ToolKind.Llvm);
        Tool? first = await toolSet.FindToolAsync(
            ToolNames.LlvmAr, cancellationToken: TestContext.Current.CancellationToken);
        File.Delete(sharedArchiver);
        Tool? second = await toolSet.FindToolAsync(
            ToolNames.LlvmAr, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(sharedArchiver, first?.Path);
        Assert.Equal(Path.Combine(slot, "llvm-ar"), second?.Path);
    }

    [Fact]
    public async Task SharedUnversionedLlvmToolFromAnotherVersionIsRejected()
    {
        RequireUnix();
        using var installation = new SyntheticInstallation();
        string shared = installation.Directory("usr/bin");
        installation.Compiler("usr/bin/clang-18", CompilerIdentity.Llvm, "18.1.8");
        installation.Compiler("usr/bin/clang++-18", CompilerIdentity.Llvm, "18.1.8");
        installation.VersionTool("usr/bin/llvm-ar", "LLVM version 22.0.0");

        ToolSet toolSet = await FindToolSetAsync(shared, ToolKind.Llvm);
        Tool? archiver = await toolSet.FindToolAsync(
            ToolNames.LlvmAr, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(archiver);
    }

    [Fact]
    public async Task AdjacentMajorSuffixesAreResolvedWithinPrivateSlot()
    {
        RequireUnix();
        using var installation = new SyntheticInstallation();
        string bin = installation.Directory("slot/bin");
        string compiler = installation.Compiler(
            "slot/bin/clang18", CompilerIdentity.Llvm, "18.1.8");
        string cppCompiler = installation.Compiler(
            "slot/bin/clang++18", CompilerIdentity.Llvm, "18.1.8");
        string archiver = installation.VersionTool(
            "slot/bin/llvm-ar18", "LLVM version 18.1.8");

        ToolSet toolSet = await FindToolSetAsync(bin, ToolKind.Llvm);
        Tool? resolvedCpp = await toolSet.FindToolAsync(
            ToolNames.Clangxx,
            cancellationToken: TestContext.Current.CancellationToken);
        Tool? resolvedArchiver = await toolSet.FindToolAsync(
            ToolNames.LlvmAr,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(compiler, toolSet.CompilerPath);
        Assert.Equal(cppCompiler, resolvedCpp?.Path);
        Assert.Equal(archiver, resolvedArchiver?.Path);
    }

    [Fact]
    public async Task HomebrewGccUsesUniformMajorSuffixAndTargetBinutils()
    {
        RequireUnix();
        using var installation = new SyntheticInstallation();
        string bin = installation.Directory("opt/gcc@14/bin");
        installation.Compiler("opt/gcc@14/bin/gcc-14", CompilerIdentity.Gnu, "14.2.0");
        installation.Compiler("opt/gcc@14/bin/g++-14", CompilerIdentity.Gnu, "14.2.0");
        installation.VersionTool("opt/gcc@14/bin/gcc-ar-14", "gcc-ar (GCC) 14.2.0");
        installation.VersionTool("opt/gcc@14/bin/gcc-ranlib-14", "gcc-ranlib (GCC) 14.2.0");
        installation.VersionTool("opt/gcc@14/bin/x86_64-linux-gnu-ld", "GNU ld 2.42");

        ToolSet toolSet = await FindToolSetAsync(Path.Combine(bin, "gcc-14"), ToolKind.Gnu);
        Tool? cppCompiler = await toolSet.FindToolAsync(
            ToolNames.Gxx, cancellationToken: TestContext.Current.CancellationToken);
        Tool? archiver = await toolSet.FindToolAsync(
            ToolNames.GccAr, cancellationToken: TestContext.Current.CancellationToken);
        Tool? linker = await toolSet.FindToolAsync(
            ToolNames.Ld, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(bin, "g++-14"), cppCompiler?.Path);
        Assert.Equal(Path.Combine(bin, "gcc-ar-14"), archiver?.Path);
        Assert.Equal(Path.Combine(bin, "x86_64-linux-gnu-ld"), linker?.Path);
    }

    [Theory]
    [InlineData("CC", "gcc-14")]
    [InlineData("CXX", "g++-14")]
    public async Task EnvironmentCompilerPathOutranksHomebrewPrefixAliases(
        string environmentVariable,
        string compilerName)
    {
        RequireUnix();
        using var installation = new SyntheticInstallation();
        string compiler = installation.Compiler(
            $"bin/{compilerName}", CompilerIdentity.Gnu, "14.2.0");
        installation.Compiler(
            "bin/aarch64-apple-darwin24-gcc-14",
            CompilerIdentity.Gnu,
            "14.2.0");
        var environment = new Dictionary<string, string?>(Environment())
        {
            [environmentVariable] = compiler,
            ["HOMEBREW_PREFIX"] = installation.RootPath,
        };
        var version = new VersionConstraint(
            exact: new Version(14, 2, 0));

        ToolSet? discoveredToolSet = await new ToolFinder(
        [
            new ToolCompilerProvider(),
        ]).FindToolSetAsync(
            new Incant.CX.FindTools.ToolSetQuery
            {
                Kind = ToolKind.Gnu,
                CompilerVersion = version,
                IncludePreview = true,
                Environment = environment,
            },
            TestContext.Current.CancellationToken);
        Incant.CX.FindSdk.Sdk? discoveredSdk = await new SdkFinder(
        [
            new SdkCompilerProvider(),
        ]).FindSdkAsync(
            new SdkQuery
            {
                Kind = SdkKind.Gnu,
                Version = version,
                TargetPlatform = TargetPlatform.Linux,
                TargetArchitecture = TargetArchitecture.X64,
                IncludePreview = true,
                Environment = environment,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            compiler,
            Assert.IsAssignableFrom<ToolSet>(
                discoveredToolSet).CompilerPath);
        Assert.Equal(compiler, discoveredSdk?.CompilerPath);
    }

    [Fact]
    public async Task GentooPrivateSlotAcceptsUnversionedCompanionTools()
    {
        RequireUnix();
        using var installation = new SyntheticInstallation();
        string bin = installation.Directory("usr/lib/llvm/18/bin");
        installation.Compiler("usr/lib/llvm/18/bin/clang", CompilerIdentity.Llvm, "18.1.8");
        installation.Compiler("usr/lib/llvm/18/bin/clang++", CompilerIdentity.Llvm, "18.1.8");
        installation.VersionTool("usr/lib/llvm/18/bin/llvm-ranlib", "LLVM version 18.1.8");

        ToolSet toolSet = await FindToolSetAsync(bin, ToolKind.Llvm);
        Tool? ranlib = await toolSet.FindToolAsync(
            ToolNames.LlvmRanlib, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(bin, "llvm-ranlib"), ranlib?.Path);
    }

    [Fact]
    public async Task CondaTargetPrefixedCcAndCxxMapToCompilerRoles()
    {
        RequireUnix();
        using var installation = new SyntheticInstallation();
        installation.Directory("conda/bin");
        string cc = installation.Compiler(
            "conda/bin/x86_64-conda-linux-gnu-cc", CompilerIdentity.Llvm, "18.1.8");
        string cxx = installation.Compiler(
            "conda/bin/x86_64-conda-linux-gnu-c++", CompilerIdentity.Llvm, "18.1.8");

        ToolSet toolSet = await FindToolSetAsync(cc, ToolKind.Llvm);
        Tool? cCompiler = await toolSet.FindToolAsync(
            ToolNames.Clang, cancellationToken: TestContext.Current.CancellationToken);
        Tool? cppCompiler = await toolSet.FindToolAsync(
            ToolNames.Clangxx, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(cc, toolSet.CompilerPath);
        Assert.Equal(cc, cCompiler?.Path);
        Assert.Equal(cxx, cppCompiler?.Path);
    }

    [Theory]
    [InlineData("nix")]
    [InlineData("spack")]
    public async Task WrapperAndSymlinkInvocationPathsArePreserved(string packageManager)
    {
        RequireUnix();
        using var installation = new SyntheticInstallation();
        string wrapper = installation.Compiler(
            "store/bin/compiler-wrapper", CompilerIdentity.Llvm, "18.1.8", "/usr/bin/env -S sh");
        string invocation = installation.FileLink(
            $"{packageManager}/bin/{packageManager}-cc", wrapper);

        IReadOnlyDictionary<string, string?> environment =
            new Dictionary<string, string?>(Environment())
            {
                ["INCANT_EXPECT_COMPILER_INVOCATION"] = invocation,
            };
        ToolSet toolSet = await FindToolSetAsync(
            invocation,
            ToolKind.Llvm,
            environment);
        Tool? compiler = await toolSet.FindToolAsync(
            ToolNames.Clang, cancellationToken: TestContext.Current.CancellationToken);
        Incant.CX.FindSdk.Sdk? sdk = await new SdkFinder(
        [
            new SdkCompilerProvider(),
        ]).FindSdkAsync(
            new SdkQuery
            {
                Kind = SdkKind.Llvm,
                CompilerPath = invocation,
                TargetPlatform = TargetPlatform.Linux,
                TargetArchitecture = TargetArchitecture.X64,
                Environment = environment,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(invocation, toolSet.CompilerPath);
        Assert.Equal(invocation, compiler?.Path);
        Assert.Equal(invocation, sdk?.CompilerPath);
    }

    [Fact]
    public async Task NixBinToolsDirectoryIsAssociatedWithNixCompiler()
    {
        RequireUnix();
        using var installation = new SyntheticInstallation();
        string compilerRoot = installation.Directory("nix/compiler");
        string binToolsRoot = installation.Directory("nix/binutils");
        string compiler = installation.Compiler(
            "nix/compiler/bin/clang", CompilerIdentity.Llvm, "18.1.8");
        string archiver = installation.VersionTool(
            "nix/binutils/bin/llvm-ar", "LLVM version 18.1.8");
        var environment = new Dictionary<string, string?>(Environment())
        {
            ["NIX_CC"] = compilerRoot,
            ["NIX_BINTOOLS"] = binToolsRoot,
        };

        ToolSet toolSet = await FindToolSetAsync(
            compiler,
            ToolKind.Llvm,
            environment);
        Tool? tool = await toolSet.FindToolAsync(
            ToolNames.LlvmAr,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(archiver, tool?.Path);
    }

    [Fact]
    public async Task RecognizedCompilerWithDifferentFamilyReturnsEmptyResult()
    {
        RequireUnix();
        using var installation = new SyntheticInstallation();
        string compiler = installation.Compiler(
            "mismatch/bin/clang", CompilerIdentity.Llvm, "18.1.8");

        Incant.CX.FindTools.DiscoveryResult toolResult =
            await new ToolFinder(
            [
                new ToolCompilerProvider(),
            ]).FindToolSetsAsync(
                new Incant.CX.FindTools.ToolSetQuery
                {
                    Kind = ToolKind.Gnu,
                    RootPath = compiler,
                    IncludePreview = true,
                    Environment = Environment(),
                },
                TestContext.Current.CancellationToken);
        Incant.CX.FindSdk.DiscoveryResult sdkResult =
            await new SdkFinder(
            [
                new SdkCompilerProvider(),
            ]).FindSdksAsync(
                new SdkQuery
                {
                    Kind = SdkKind.Gnu,
                    RootPath = compiler,
                    IncludePreview = true,
                    Environment = Environment(),
                },
                TestContext.Current.CancellationToken);

        Assert.Empty(toolResult.ToolSets);
        Assert.Contains(
            toolResult.Diagnostics,
            diagnostic =>
                diagnostic.Code == "different-toolset-family");
        Assert.Empty(sdkResult.Sdks);
        Assert.Contains(
            sdkResult.Diagnostics,
            diagnostic => diagnostic.Code == "different-sdk-family");
    }

    [Fact]
    public async Task FatMachOCompanionSelectsTheRequestedSlice()
    {
        RequireUnix();
        using var installation = new SyntheticInstallation();
        string bin = installation.Directory("fat/bin");
        installation.Compiler("fat/bin/clang", CompilerIdentity.Llvm, "18.1.8");
        installation.Compiler("fat/bin/clang++", CompilerIdentity.Llvm, "18.1.8");
        installation.FatMachO(
            "fat/bin/llvm-ar", TargetArchitecture.ARM64, TargetArchitecture.X64);

        ToolSet toolSet = await FindToolSetAsync(bin, ToolKind.Llvm);
        Tool? preferred = await toolSet.FindToolAsync(
            ToolNames.LlvmAr,
            cancellationToken: TestContext.Current.CancellationToken);
        Tool? arm64 = await toolSet.FindToolAsync(
            ToolNames.LlvmAr,
            new ToolQuery { HostArchitecture = TargetArchitecture.ARM64 },
            TestContext.Current.CancellationToken);
        Tool? x64 = await toolSet.FindToolAsync(
            ToolNames.LlvmAr,
            new ToolQuery { HostArchitecture = TargetArchitecture.X64 },
            TestContext.Current.CancellationToken);

        TargetArchitecture current = CurrentArchitecture();
        Assert.Equal(
            current is TargetArchitecture.ARM64 or TargetArchitecture.X64
                ? current
                : TargetArchitecture.Unknown,
            preferred?.HostArchitecture);
        Assert.Equal(TargetArchitecture.ARM64, arm64?.HostArchitecture);
        Assert.Equal(TargetArchitecture.X64, x64?.HostArchitecture);
    }

    [Fact]
    public async Task ShebangWrapperReportsItsInterpreterArchitecture()
    {
        RequireUnix();
        using var installation = new SyntheticInstallation();
        string wrapper = installation.Compiler(
            "wrapper/bin/custom-compiler", CompilerIdentity.Llvm, "18.1.8", "/usr/bin/env -S sh");

        ToolSet toolSet = await FindToolSetAsync(wrapper, ToolKind.Llvm);
        Tool? compiler = await toolSet.FindToolAsync(
            ToolNames.Clang, cancellationToken: TestContext.Current.CancellationToken);
        Tool? cppCompiler = await toolSet.FindToolAsync(
            ToolNames.Clangxx, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CurrentArchitecture(), compiler?.HostArchitecture);
        Assert.Equal(wrapper, cppCompiler?.Path);
    }

    [Theory]
    [InlineData("x86_64-vendor-linux-gnu-clang-18.1", "x86_64-vendor-linux-gnu-clang++-18.1")]
    [InlineData("clang18.1", "clang++18.1")]
    [InlineData("cc-18.1", "c++-18.1")]
    public async Task DirectoryDiscoveryAndCompanionLookupShareVersionedDriverNames(string cName, string cppName)
    {
        RequireUnix();
        using var installation = new SyntheticInstallation();
        string bin = installation.Directory("slot/bin");
        string compiler = installation.Compiler("slot/bin/" + cName, CompilerIdentity.Llvm, "18.1.8");
        string cpp = installation.Compiler("slot/bin/" + cppName, CompilerIdentity.Llvm, "18.1.8");

        ToolSet toolSet = await FindToolSetAsync(bin, ToolKind.Llvm);
        Tool? companion = await toolSet.FindToolAsync(ToolNames.Clangxx,
            cancellationToken: TestContext.Current.CancellationToken);
        Incant.CX.FindSdk.Sdk? sdk = await new SdkFinder([new SdkCompilerProvider()])
            .FindSdkAsync(new SdkQuery
            {
                Kind = SdkKind.Llvm,
                RootPath = bin,
                Environment = Environment(),
            }, TestContext.Current.CancellationToken);

        Assert.Equal(compiler, toolSet.CompilerPath);
        Assert.Equal(cpp, companion?.Path);
        Assert.Equal(compiler, sdk?.CompilerPath);
    }

    [Fact]
    public async Task CompilerFamilyComesFromTheDriverRatherThanItsInvocationName()
    {
        RequireUnix();
        using var installation = new SyntheticInstallation();
        string compiler = installation.Compiler("slot/bin/gcc-18", CompilerIdentity.Llvm, "18.1.8");
        ToolSet toolSet = await FindToolSetAsync(compiler, ToolKind.Llvm);
        Incant.CX.FindSdk.Sdk? sdk = await new SdkFinder([new SdkCompilerProvider()])
            .FindSdkAsync(new SdkQuery
            {
                Kind = SdkKind.Llvm,
                CompilerPath = compiler,
                Environment = Environment(),
            }, TestContext.Current.CancellationToken);
        Tool? cCompiler = await toolSet.FindToolAsync(ToolNames.Clang,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(compiler, cCompiler?.Path);
        Assert.Equal(ToolKind.Llvm, toolSet.Kind);
        Assert.Equal(SdkKind.Llvm, sdk?.Kind);
        Assert.Equal(toolSet.CompilerVersion, sdk?.Version);
    }

    private static async Task<ToolSet> FindToolSetAsync(
        string root,
        ToolKind kind,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ToolSet? toolSet = await new ToolFinder(
        [
            new ToolCompilerProvider(),
        ]).FindToolSetAsync(
            new Incant.CX.FindTools.ToolSetQuery
            {
                Kind = kind,
                RootPath = root,
                IncludePreview = true,
                Environment = environment ?? Environment(),
            },
            TestContext.Current.CancellationToken);
        return Assert.IsAssignableFrom<ToolSet>(toolSet);
    }

    private static IReadOnlyDictionary<string, string?> Environment() =>
        new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin:/bin",
        };

    private static TargetArchitecture CurrentArchitecture() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => TargetArchitecture.X86,
            Architecture.X64 => TargetArchitecture.X64,
            Architecture.Arm => TargetArchitecture.ARM,
            Architecture.Arm64 => TargetArchitecture.ARM64,
            _ => TargetArchitecture.Unknown,
        };

    private static void RequireUnix() =>
        Assert.SkipUnless(
            !OperatingSystem.IsWindows(),
            "Synthetic compiler layouts use controlled Unix launchers.");

    private enum CompilerIdentity
    {
        Gnu,
        Llvm,
    }

    private sealed class SyntheticInstallation : IDisposable
    {
        private readonly TestDirectory _directory = new();

        internal string RootPath => _directory.Root;

        internal string Directory(string relativePath)
        {
            string path = Path.Combine(RootPath, Native(relativePath));
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        internal string Compiler(
            string relativePath,
            CompilerIdentity identity,
            string version,
            string shebang = "/bin/sh")
        {
            string path = Path.Combine(RootPath, Native(relativePath));
            string? directory = Path.GetDirectoryName(path);
            System.IO.Directory.CreateDirectory(directory!);
            string resource = Directory("resources");
            string include = Directory("include");
            string library = Directory("lib");
            string identityText = identity == CompilerIdentity.Llvm
                ? $"clang version {version}"
                : $"gcc (GCC) {version} Free Software Foundation";
            string escapedResource = ShellLiteral(resource);
            string escapedInclude = ShellLiteral(include);
            string escapedLibrary = ShellLiteral(library);
            string content = $$"""
#!{{shebang}}
if [ -n "${INCANT_EXPECT_COMPILER_INVOCATION:-}" ] && [ "$0" != "$INCANT_EXPECT_COMPILER_INVOCATION" ]; then
  exit 97
fi
case "$1" in
  --version)
    printf '%s\n' '{{identityText}}'
    exit 0
    ;;
  -dumpmachine|-print-target-triple)
    printf '%s\n' 'x86_64-linux-gnu'
    exit 0
    ;;
  -dumpfullversion|-dumpversion)
    printf '%s\n' '{{version}}'
    exit 0
    ;;
  -print-multi-lib)
    printf '.;\n'
    exit 0
    ;;
  -print-multi-directory)
    printf '.\n'
    exit 0
    ;;
  -print-multiarch)
    printf '%s\n' 'x86_64-linux-gnu'
    exit 0
    ;;
  -print-sysroot)
    printf '\n'
    exit 0
    ;;
  -print-resource-dir|-print-file-name=include)
    printf '%s\n' '{{escapedResource}}'
    exit 0
    ;;
  -print-search-dirs)
    printf 'libraries: =%s\n' '{{escapedLibrary}}'
    exit 0
    ;;
  -print-file-name=*)
    printf '%s\n' "${1#*=}"
    exit 0
    ;;
  -print-prog-name=*|--print-prog-name=*)
    printf '%s\n' "${1#*=}"
    exit 0
    ;;
esac
for argument in "$@"; do
  if [ "$argument" = '-dM' ]; then
    printf '%s\n' '#define __x86_64__ 1'
    exit 0
  fi
  if [ "$argument" = '-v' ]; then
    printf '%s\n' '#include <...> search starts here:' >&2
    printf ' %s\n' '{{escapedInclude}}' >&2
    printf '%s\n' 'End of search list.' >&2
    exit 0
  fi
done
exit 0
""";
            WriteScript(path, content);
            return path;
        }

        internal string VersionTool(string relativePath, string version)
        {
            string path = Path.Combine(RootPath, Native(relativePath));
            string? directory = Path.GetDirectoryName(path);
            System.IO.Directory.CreateDirectory(directory!);
            WriteScript(
                path,
                $$"""
#!/bin/sh
printf '%s\n' '{{version}}'
""");
            return path;
        }

        internal string FileLink(string relativePath, string target)
        {
            string path = Path.Combine(RootPath, Native(relativePath));
            string? directory = Path.GetDirectoryName(path);
            System.IO.Directory.CreateDirectory(directory!);
            File.CreateSymbolicLink(path, target);
            return path;
        }

        internal void FatMachO(string relativePath, params TargetArchitecture[] architectures)
        {
            string path = Path.Combine(RootPath, Native(relativePath));
            string? directory = Path.GetDirectoryName(path);
            System.IO.Directory.CreateDirectory(directory!);
            byte[] image = new byte[8 + architectures.Length * 20];
            BinaryPrimitives.WriteUInt32BigEndian(image, 0xcafebabe);
            BinaryPrimitives.WriteUInt32BigEndian(
                image.AsSpan(4), (uint)architectures.Length);
            for (int index = 0; index < architectures.Length; index++)
            {
                uint cpuType = architectures[index] switch
                {
                    TargetArchitecture.X64 => 0x01000007,
                    TargetArchitecture.ARM64 => 0x0100000c,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(architectures), architectures[index], null),
                };
                BinaryPrimitives.WriteUInt32BigEndian(
                    image.AsSpan(8 + index * 20), cpuType);
            }

            File.WriteAllBytes(path, image);
        }

        public void Dispose() => _directory.Dispose();

        private static void WriteScript(string path, string content)
        {
            File.WriteAllText(path, content.ReplaceLineEndings("\n"));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead
                        | UnixFileMode.UserWrite
                        | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead
                        | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead
                        | UnixFileMode.OtherExecute);
            }
        }

        private static string Native(string path) =>
            path.Replace('/', Path.DirectorySeparatorChar);

        private static string ShellLiteral(string value) =>
            value.Replace("'", "'\"'\"'", StringComparison.Ordinal);
    }
}
