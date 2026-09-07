namespace Incant.AutoTest.CppToolchain;

internal static class LibraryChainScenario
{
    internal static string AddStaticLibrary(
        BuildPlanBuilder builder, ResolvedToolchain toolchain,
        string staticC, string staticExtra, string staticCpp, string archive,
        Func<bool, string, string, IReadOnlyList<string>> compile)
    {
        foreach ((string id, bool cpp, string source, string output) in new[]
        {
            ("compile-static-c", false, FixturePaths.StaticC, staticC),
            ("compile-static-extra", false, FixturePaths.StaticExtra, staticExtra),
            ("compile-static-cpp", true, FixturePaths.StaticCpp, staticCpp),
        })
        {
            builder.Add(id, BuildActionPhase.Build,
                cpp ? toolchain.CppCompiler.Path : toolchain.CCompiler.Path,
                compile(cpp, source, output), artifacts: [output]);
        }

        bool librarian = toolchain.Archiver.Name is "lib" or "llvm-lib";
        builder.Add("archive-static", BuildActionPhase.Build, toolchain.Archiver.Path,
            librarian ? ["/NOLOGO", "/OUT:" + archive, staticC, staticExtra, staticCpp]
                : ["rcs", archive, staticC, staticExtra, staticCpp],
            ["compile-static-c", "compile-static-extra", "compile-static-cpp"], [archive]);
        string archiveReady = "archive-static";
        if (toolchain.Ranlib is not null)
        {
            builder.Add("index-static", BuildActionPhase.Build, toolchain.Ranlib.Path,
                [archive], [archiveReady], [archive]);
            archiveReady = "index-static";
        }

        builder.Add("inspect-static", BuildActionPhase.Build, toolchain.Archiver.Path,
            librarian ? ["/NOLOGO", "/LIST", archive] : ["t", archive],
            [archiveReady], outputFragments:
                [Path.GetFileName(staticC), Path.GetFileName(staticExtra), Path.GetFileName(staticCpp)]);
        return "inspect-static";
    }
}
