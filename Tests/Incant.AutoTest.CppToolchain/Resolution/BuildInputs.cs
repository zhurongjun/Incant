using Incant.Core.Cpp.FindSdk;

namespace Incant.AutoTest.CppToolchain;

internal static class BuildInputs
{
    internal static bool ContainsFile(TargetLayout layout, ResourcePurpose purpose, string name) =>
        layout.Resources.Any(resource => resource.Purpose == purpose
            && (File.Exists(resource.Path) && Path.GetFileName(resource.Path).Equals(name, StringComparison.OrdinalIgnoreCase)
                || Directory.Exists(resource.Path) && File.Exists(Path.Combine(resource.Path, name))));

    internal static bool AppleSdk(TargetLayout layout) =>
        layout.SysrootPath is string root && Directory.Exists(root)
        && ContainsFile(layout, ResourcePurpose.CInclude, "stdio.h")
        && new[] { "libSystem.tbd", "libSystem.B.tbd", "libSystem.dylib", "libSystem.B.dylib" }
            .Any(name => ContainsFile(layout, ResourcePurpose.Library, name));

    internal static bool WindowsSdk(TargetLayout layout) =>
        ContainsFile(layout, ResourcePurpose.CInclude, "stdio.h")
        && ContainsFile(layout, ResourcePurpose.Library, "kernel32.lib")
        && ContainsFile(layout, ResourcePurpose.Library, "ucrt.lib");

    internal static bool MsvcSdk(TargetLayout layout) =>
        ContainsFile(layout, ResourcePurpose.CppInclude, "array")
        && ContainsFile(layout, ResourcePurpose.Library, "msvcrt.lib")
        && ContainsFile(layout, ResourcePurpose.Library, "msvcprt.lib");

    internal static IReadOnlyList<string> Missing(ResolvedToolchain toolchain)
    {
        var missing = new List<string>();
        foreach (string path in new[] { toolchain.CCompiler.Path, toolchain.CppCompiler.Path, toolchain.Archiver.Path }
            .Concat(toolchain.Linker is null ? [] : new[] { toolchain.Linker.Path }))
        {
            if (!File.Exists(path))
            {
                missing.Add($"Tool '{path}' is absent.");
            }
        }

        if (toolchain.LinkerFlavor != LinkerFlavor.Driver && toolchain.Linker is null)
        {
            missing.Add("The explicit linker was not selected.");
        }

        foreach (ResolvedSdkComponent component in toolchain.Sdks)
        {
            if (component.Layout.SysrootPath is string root && !Directory.Exists(root))
            {
                missing.Add($"SDK sysroot '{root}' is absent.");
            }

            if (component.Sdk.Kind == Kind.Windows && !WindowsSdk(component.Layout)
                || component.Sdk.Kind == Kind.Msvc && !MsvcSdk(component.Layout))
            {
                missing.Add($"The {component.Sdk.Kind} SDK lacks headers or runtime libraries used by the library chain.");
            }
        }

        foreach ((ResourcePurpose purpose, string name) in new[]
        {
            (ResourcePurpose.CInclude, "stdio.h"),
            (ResourcePurpose.CppInclude, "array"),
        })
        {
            if (!toolchain.Sdks.Any(component => ContainsFile(component.Layout, purpose, name)))
            {
                missing.Add($"No discovered {purpose} directory supplies '{name}'.");
            }
        }

        if (toolchain.AdapterKind == BuildAdapterKind.Wasi)
        {
            string[] libraries = toolchain.Multilib == "eh"
                ? ["libc.a", "libc++.a", "libc++abi.a", "libunwind.a"]
                : ["libc.a", "libc++.a", "libc++abi.a"];
            foreach (string library in libraries)
            {
                // A parent search directory must not stand in for an inventoried variant library.
                if (!toolchain.Resources.Any(resource => resource.Purpose == ResourcePurpose.Library
                    && Path.GetFileName(resource.Path) == library && File.Exists(resource.Path)))
                {
                    missing.Add($"The selected WASI layout does not supply '{library}'.");
                }
            }
        }

        if (toolchain.ExecutionMode is ExecutionMode.Node or ExecutionMode.Wasmtime
            && !File.Exists(toolchain.RuntimePath))
        {
            missing.Add($"The {toolchain.ExecutionMode} runtime is unavailable.");
        }

        return missing;
    }
}
