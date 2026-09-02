using Incant.Base;

namespace Incant.Core.Toolchains;

/// <summary>Discovers Xcode and Apple platform SDK installations.</summary>
public sealed class XcodeDiscoveryProvider : IDiscoveryProvider
{
    private static readonly IReadOnlyCollection<Kind> s_kinds = Array.AsReadOnly(
        [Kind.Xcode, Kind.Llvm]);

    /// <inheritdoc />
    public string Name => "Xcode";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds => s_kinds;

    /// <inheritdoc />
    public async ValueTask<DiscoveryResult> DiscoverAsync(
        DiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.HostOS != PlatformOS.OSX)
        {
            return new DiscoveryResult();
        }

        var candidates = new List<Candidate>();
        var seenPaths = new HashSet<string>(ProviderUtilities.GetPathComparer());
        foreach (PathHint hint in context.GetExplicitPaths(Kind.Xcode))
        {
            AddCandidate(candidates, seenPaths, hint.Path, Source.Explicit);
        }

        AddCandidate(
            candidates,
            seenPaths,
            context.GetEnvironmentVariable("DEVELOPER_DIR"),
            Source.Environment);
        ProcessResult? selectedDeveloperDirectory = await ProviderUtilities.TryRunProbeAsync(
            "/usr/bin/xcode-select",
            ["-p"],
            context,
            cancellationToken).ConfigureAwait(false);
        AddCandidate(
            candidates,
            seenPaths,
            selectedDeveloperDirectory?.StandardOutput.Trim(),
            Source.Vendor);
        AddStandardCandidates(candidates, seenPaths);

        var toolchains = new List<Installation>();
        var sdks = new List<SdkInstallation>();
        var diagnostics = new List<Diagnostic>();
        foreach (Candidate candidate in candidates)
        {
            (Installation? toolchain, IReadOnlyList<SdkInstallation> candidateSdks) =
                await InspectAsync(candidate, context, cancellationToken).ConfigureAwait(false);
            if (toolchain is null || candidateSdks.Count == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "invalid-candidate",
                    Name,
                    "The developer directory does not provide a complete Apple Clang toolchain and SDK.",
                    candidate.Path));
                continue;
            }

            toolchains.Add(toolchain);
            sdks.AddRange(candidateSdks);
        }

        return new DiscoveryResult(toolchains, sdks, diagnostics);
    }

    private static async Task<(Installation? Installation, IReadOnlyList<SdkInstallation> Sdks)>
        InspectAsync(
            Candidate candidate,
            DiscoveryContext context,
            CancellationToken cancellationToken)
    {
        if (!Directory.Exists(candidate.Path))
        {
            return (null, []);
        }

        IReadOnlyDictionary<string, string?> environment = new Dictionary<string, string?>
        {
            ["DEVELOPER_DIR"] = candidate.Path,
        };
        string? clang = await FindWithXcrunAsync(
            "clang",
            context,
            environment,
            cancellationToken).ConfigureAwait(false);
        string? cppCompiler = await FindWithXcrunAsync(
            "clang++",
            context,
            environment,
            cancellationToken).ConfigureAwait(false);
        string? archiver = await FindWithXcrunAsync(
            "ar",
            context,
            environment,
            cancellationToken).ConfigureAwait(false);
        string? linker = await FindWithXcrunAsync(
            "ld",
            context,
            environment,
            cancellationToken).ConfigureAwait(false);
        if (clang is null || cppCompiler is null || archiver is null || linker is null)
        {
            return (null, []);
        }

        ProcessResult? compilerVersionResult = await ProviderUtilities.TryRunProbeAsync(
            clang,
            ["--version"],
            context,
            cancellationToken,
            environment).ConfigureAwait(false);
        ProcessResult? productVersionResult = await ProviderUtilities.TryRunProbeAsync(
            "/usr/bin/xcodebuild",
            ["-version"],
            context,
            cancellationToken,
            environment).ConfigureAwait(false);
        Version? compilerVersion = ProviderUtilities.ParseVersion(compilerVersionResult?.StandardOutput);
        Version? productVersion = ProviderUtilities.ParseVersion(productVersionResult?.StandardOutput);

        var sdks = new List<SdkInstallation>();
        foreach (AppleSdkDefinition definition in GetSdkDefinitions())
        {
            SdkInstallation? sdk = await InspectSdkAsync(
                candidate,
                definition,
                context,
                environment,
                cancellationToken).ConfigureAwait(false);
            if (sdk is not null)
            {
                sdks.Add(sdk);
            }
        }

        TargetPlatform[] platforms = sdks
            .Select(sdk => sdk.TargetPlatform)
            .Distinct()
            .ToArray();
        TargetArchitecture[] architectures = sdks
            .SelectMany(sdk => sdk.TargetArchitectures)
            .Distinct()
            .ToArray();
        var components = new Component[]
        {
            new(ComponentKind.Compiler, clang, context.HostArchitecture),
            new(ComponentKind.CppCompiler, cppCompiler, context.HostArchitecture),
            new(ComponentKind.Archiver, archiver, context.HostArchitecture),
            new(ComponentKind.Linker, linker, context.HostArchitecture),
        };
        var toolchain = new Installation(
            Kind.Xcode,
            CompilerFamily.Clang,
            candidate.Path,
            context.HostOS,
            context.HostArchitecture,
            productVersion,
            compilerVersion,
            ProviderUtilities.GetChannel(
                GetProductPath(candidate.Path),
                productVersionResult?.StandardOutput),
            candidate.Sources,
            platforms,
            architectures,
            components);
        return (toolchain, sdks);
    }

    private static string GetProductPath(string developerDirectory)
    {
        DirectoryInfo? contentsDirectory = Directory.GetParent(developerDirectory);
        DirectoryInfo? productDirectory = contentsDirectory?.Parent;
        return string.Equals(contentsDirectory?.Name, "Contents", StringComparison.OrdinalIgnoreCase)
            && productDirectory?.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true
                ? productDirectory.FullName
                : developerDirectory;
    }

    private static async Task<SdkInstallation?> InspectSdkAsync(
        Candidate candidate,
        AppleSdkDefinition definition,
        DiscoveryContext context,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        ProcessResult? pathResult = await ProviderUtilities.TryRunProbeAsync(
            "/usr/bin/xcrun",
            ["--sdk", definition.Name, "--show-sdk-path"],
            context,
            cancellationToken,
            environment).ConfigureAwait(false);
        ProcessResult? versionResult = await ProviderUtilities.TryRunProbeAsync(
            "/usr/bin/xcrun",
            ["--sdk", definition.Name, "--show-sdk-version"],
            context,
            cancellationToken,
            environment).ConfigureAwait(false);
        string? sysroot = pathResult?.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(sysroot) || !Directory.Exists(sysroot))
        {
            return null;
        }

        return new SdkInstallation(
            Kind.Xcode,
            definition.Platform,
            candidate.Path,
            sysroot,
            ProviderUtilities.ParseVersion(versionResult?.StandardOutput),
            candidate.Sources,
            definition.Architectures);
    }

    private static async Task<string?> FindWithXcrunAsync(
        string tool,
        DiscoveryContext context,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        ProcessResult? result = await ProviderUtilities.TryRunProbeAsync(
            "/usr/bin/xcrun",
            ["--find", tool],
            context,
            cancellationToken,
            environment).ConfigureAwait(false);
        string? path = result?.StandardOutput.Trim();
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    private static void AddStandardCandidates(
        ICollection<Candidate> candidates,
        ISet<string> seenPaths)
    {
        const string Applications = "/Applications";
        if (Directory.Exists(Applications))
        {
            try
            {
                foreach (string application in Directory.EnumerateDirectories(Applications, "Xcode*.app"))
                {
                    AddCandidate(candidates, seenPaths, application, Source.StandardPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        AddCandidate(
            candidates,
            seenPaths,
            "/Library/Developer/CommandLineTools",
            Source.StandardPath);
    }

    private static void AddCandidate(
        ICollection<Candidate> candidates,
        ISet<string> seenPaths,
        string? path,
        Source source)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized = path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(path, "Contents", "Developer")
            : path;
        ProviderUtilities.AddCandidate(candidates, seenPaths, normalized, source);
    }

    private static IEnumerable<AppleSdkDefinition> GetSdkDefinitions()
    {
        yield return new AppleSdkDefinition(
            "macosx",
            TargetPlatform.MacOS,
            [TargetArchitecture.X64, TargetArchitecture.ARM64]);
        yield return new AppleSdkDefinition(
            "iphoneos",
            TargetPlatform.IOS,
            [TargetArchitecture.ARM64]);
        yield return new AppleSdkDefinition(
            "iphonesimulator",
            TargetPlatform.IOSSimulator,
            [TargetArchitecture.X64, TargetArchitecture.ARM64]);
        yield return new AppleSdkDefinition(
            "appletvos",
            TargetPlatform.TvOS,
            [TargetArchitecture.ARM64]);
        yield return new AppleSdkDefinition(
            "appletvsimulator",
            TargetPlatform.TvOSSimulator,
            [TargetArchitecture.X64, TargetArchitecture.ARM64]);
        yield return new AppleSdkDefinition(
            "watchos",
            TargetPlatform.WatchOS,
            [TargetArchitecture.ARM64]);
        yield return new AppleSdkDefinition(
            "watchsimulator",
            TargetPlatform.WatchOSSimulator,
            [TargetArchitecture.X64, TargetArchitecture.ARM64]);
        yield return new AppleSdkDefinition(
            "xros",
            TargetPlatform.VisionOS,
            [TargetArchitecture.ARM64]);
        yield return new AppleSdkDefinition(
            "xrsimulator",
            TargetPlatform.VisionOSSimulator,
            [TargetArchitecture.ARM64]);
    }

    private sealed record AppleSdkDefinition(
        string Name,
        TargetPlatform Platform,
        IReadOnlyList<TargetArchitecture> Architectures);
}
