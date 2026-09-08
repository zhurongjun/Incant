namespace Incant.AutoTest.CXLegacyToolchain.Setup;

internal static class BundleToolchainComponents
{
    internal static IReadOnlyList<ISetupComponent> Create(EnvironmentDefinition profile)
    {
        var components = new List<ISetupComponent>();
        foreach (InstallationRequirement requirement in profile.Installations)
        {
            ISetupComponent? component = requirement.Kind switch
            {
                InstallationKind.AndroidNdk => new AndroidNdkComponent(
                    RequireSingle(
                        BundleCatalog.Android,
                        release => release.Id == requirement.Id,
                        requirement.Id)),
                InstallationKind.Emscripten => new EmscriptenComponent(
                    RequireSingle(
                        BundleCatalog.Emscripten,
                        release => $"emscripten-{release.Version}" == requirement.Id,
                        requirement.Id)),
                InstallationKind.WasiSdk => new WasiSdkComponent(
                    RequireSingle(
                        BundleCatalog.Wasi,
                        release => $"wasi-sdk-{release.Version}" == requirement.Id,
                        requirement.Id)),
                _ => null,
            };
            if (component is not null)
            {
                components.Add(component);
            }
        }

        if (profile.Installations.Any(
            requirement => requirement.Kind == InstallationKind.WasiSdk))
        {
            components.Add(new WasmtimeComponent(BundleCatalog.Wasmtime));
        }

        return components;
    }

    private static T RequireSingle<T>(
        IEnumerable<T> candidates,
        Func<T, bool> predicate,
        string requirementId)
    {
        T[] matches = candidates.Where(predicate).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new SetupConfigurationException(
                $"Bundle catalog entry '{requirementId}' resolved to {matches.Length} records.");
    }
}
