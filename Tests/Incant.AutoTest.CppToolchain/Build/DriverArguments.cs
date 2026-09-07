using Incant.Core.Cpp;

namespace Incant.AutoTest.CppToolchain;

internal static class DriverArguments
{
    internal static IReadOnlyList<string> DriverCompileArguments(
        ResolvedToolchain toolchain,
        bool cpp,
        bool positionIndependent)
    {
        var arguments = new List<string>();
        arguments.AddRange(TargetArguments(toolchain));
        arguments.AddRange(MultilibArguments(toolchain.Multilib));
        arguments.Add(cpp ? "-std=c++17" : "-std=c11");
        if (positionIndependent)
        {
            arguments.Add("-fPIC");
        }

        arguments.AddRange(["-I", FixturePaths.Root]);
        foreach (string directory in DriverResourceArguments.IncludeDirectories(
            toolchain, cpp))
        {
            arguments.AddRange(["-isystem", directory]);
        }

        foreach (string framework in DriverResourceArguments.FrameworkDirectories(
            toolchain))
        {
            arguments.AddRange(["-F", framework]);
        }

        return arguments;
    }

    internal static IReadOnlyList<string> DriverLinkArguments(
        ResolvedToolchain toolchain)
    {
        var arguments = new List<string>();
        arguments.AddRange(TargetArguments(toolchain));
        arguments.AddRange(MultilibArguments(toolchain.Multilib));
        foreach (string directory in DriverResourceArguments.LinkDirectories(
            toolchain))
        {
            arguments.AddRange(["-L", directory]);
        }

        foreach (string framework in DriverResourceArguments.FrameworkDirectories(
            toolchain))
        {
            arguments.AddRange(["-F", framework]);
        }

        return arguments;
    }

    internal static IReadOnlyList<string> TargetArguments(
        ResolvedToolchain toolchain)
    {
        string? sysroot = toolchain.Sdks
            .OrderByDescending(component => component.Role is "platform" or "bundle")
            .Select(component => component.Layout.SysrootPath)
            .FirstOrDefault(path => path is not null);
        var arguments = new List<string>();
        switch (toolchain.AdapterKind)
        {
            case BuildAdapterKind.Gnu:
                if (sysroot is not null)
                {
                    if (toolchain.TargetPlatform == TargetPlatform.MacOS)
                    {
                        arguments.AddRange(["-isysroot", sysroot]);
                    }
                    else
                    {
                        arguments.Add("--sysroot=" + sysroot);
                    }
                }

                break;
            case BuildAdapterKind.Llvm:
                arguments.Add("--target=" + toolchain.TargetTriple);
                if (sysroot is not null)
                {
                    arguments.Add(toolchain.TargetPlatform == TargetPlatform.MacOS
                        ? "-isysroot"
                        : "--sysroot=" + sysroot);
                    if (toolchain.TargetPlatform == TargetPlatform.MacOS)
                    {
                        arguments.Add(sysroot);
                    }
                }

                break;
            case BuildAdapterKind.Apple:
                arguments.AddRange(["-target", toolchain.TargetTriple]);
                if (sysroot is not null)
                {
                    arguments.AddRange(["-isysroot", sysroot]);
                }

                break;
            case BuildAdapterKind.Android:
                string androidTarget = toolchain.TargetTriple
                    + toolchain.AndroidApi!.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                arguments.Add("--target=" + androidTarget);
                if (sysroot is not null)
                {
                    arguments.Add("--sysroot=" + sysroot);
                }

                break;
            case BuildAdapterKind.Wasi:
                arguments.Add("--target=" + toolchain.TargetTriple);
                if (sysroot is not null)
                {
                    arguments.Add("--sysroot=" + sysroot);
                }

                break;
            case BuildAdapterKind.Emscripten:
                if (sysroot is not null)
                {
                    arguments.Add("--sysroot=" + sysroot);
                }

                break;
        }

        return arguments;
    }

    internal static IReadOnlyList<string> MultilibArguments(string? multilib) =>
        multilib switch
        {
            "32" => ["-m32"],
            "64" => ["-m64"],
            "x32" => ["-mx32"],
            _ => [],
        };
}
