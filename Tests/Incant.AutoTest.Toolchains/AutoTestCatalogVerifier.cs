using Incant.Core.Toolchains;

/// <summary>Applies AutoTest constraints to a discovered catalog and resolves one smoke-test profile.</summary>
internal static class AutoTestCatalogVerifier
{
    /// <summary>Verifies installation counts and components, then returns the selected profile.</summary>
    internal static Profile Verify(
        AutoTestCommand command,
        Catalog catalog,
        string source)
    {
        Kind kind = command.Kind
            ?? throw new InvalidOperationException("A verify command must contain a toolchain kind.");
        bool sdkOnlyKind = kind == Kind.WindowsSdk;
        Installation[] matchingToolchains = catalog.Installations
            .Where(toolchain => toolchain.Kind == kind)
            .Where(toolchain => MatchesMajor(toolchain.ProductVersion, command.ProductMajor))
            .Where(toolchain => MatchesMajor(toolchain.CompilerVersion, command.CompilerMajor))
            .ToArray();
        SdkInstallation[] matchingSdks = catalog.Sdks
            .Where(sdk => sdk.Kind == kind)
            .Where(sdk => command.Target is null || sdk.TargetPlatform == command.Target)
            .Where(sdk => command.Architecture is null
                || sdk.TargetArchitectures.Contains(command.Architecture.Value))
            .Where(sdk => MatchesMajor(sdk.Version, command.SdkMajor))
            .ToArray();

        int candidateCount = sdkOnlyKind ? matchingSdks.Length : matchingToolchains.Length;
        if (candidateCount < command.MinimumCount)
        {
            throw new AutoTestFailureException(
                $"{source} found {candidateCount} matching {kind} installation(s); "
                + $"at least {command.MinimumCount} were required.");
        }

        Profile profile = Resolver.Resolve(
            catalog,
            CreateSelection(command, sdkOnlyKind));
        if (!sdkOnlyKind && !ContainsRequiredComponents(profile.Installation, command.RequiredComponents))
        {
            throw new AutoTestFailureException(
                $"{source} resolved a {kind} profile that does not contain every required component.");
        }

        Console.WriteLine(
            $"Verified {candidateCount} matching {kind} installation(s) from {source}; "
            + $"selected {profile.TargetPlatform}/{profile.TargetArchitecture}.");
        return profile;
    }

    private static Selection CreateSelection(AutoTestCommand command, bool sdkOnlyKind)
    {
        Kind requestedKind = command.Kind!.Value;
        Kind? sdkKind = requestedKind switch
        {
            Kind.WindowsSdk => Kind.WindowsSdk,
            Kind.VisualStudio => Kind.WindowsSdk,
            Kind.Llvm when command.Target == TargetPlatform.Windows =>
                Kind.WindowsSdk,
            Kind.Xcode => Kind.Xcode,
            Kind.AndroidNdk => Kind.AndroidNdk,
            Kind.Emscripten => Kind.Emscripten,
            Kind.WasiSdk => Kind.WasiSdk,
            _ => null,
        };

        return new Selection
        {
            Kind = sdkOnlyKind ? Kind.VisualStudio : requestedKind,
            SdkKind = sdkKind,
            TargetPlatform = command.Target,
            TargetArchitecture = command.Architecture,
            ProductVersion = sdkOnlyKind ? null : CreateMajorConstraint(command.ProductMajor),
            CompilerVersion = sdkOnlyKind ? null : CreateMajorConstraint(command.CompilerMajor),
            SdkVersion = CreateMajorConstraint(command.SdkMajor),
            IncludePreview = command.IncludePreview,
        };
    }

    private static bool ContainsRequiredComponents(
        Installation toolchain,
        IEnumerable<ComponentKind> requiredComponents) =>
        requiredComponents.All(required =>
            toolchain.Components.Any(component => component.Kind == required));

    private static bool MatchesMajor(Version? version, int? expectedMajor) =>
        expectedMajor is null || version?.Major == expectedMajor;

    private static VersionConstraint? CreateMajorConstraint(int? major) => major is null
        ? null
        : new VersionConstraint(
            minimumInclusive: new Version(major.Value, 0),
            maximumExclusive: new Version(major.Value + 1, 0));
}
