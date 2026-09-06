namespace Incant.AutoTest.CppToolchain;

internal static class FixturePaths
{
    internal static string Root { get; } =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "LibraryChain");

    internal static string Header => Path.Combine(Root, "fixture.h");

    internal static string StaticC => Path.Combine(Root, "static_c.c");

    internal static string StaticExtra => Path.Combine(Root, "static_extra.c");

    internal static string StaticCpp => Path.Combine(Root, "static_cpp.cpp");

    internal static string SharedCpp => Path.Combine(Root, "shared.cpp");

    internal static string MainC => Path.Combine(Root, "main_c.c");

    internal static string MainCpp => Path.Combine(Root, "main.cpp");
}
