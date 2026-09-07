namespace Incant.AutoTest.CppToolchain;

internal static class BuildAdapterFactory
{
    internal static IBuildAdapter Create(ResolvedToolchain toolchain) =>
        toolchain.AdapterKind switch
        {
            BuildAdapterKind.Msvc or BuildAdapterKind.ClangCl => new WindowsBuildAdapter(),
            BuildAdapterKind.Gnu or BuildAdapterKind.Llvm or BuildAdapterKind.Apple
                or BuildAdapterKind.Android => new UnixDriverBuildAdapter(),
            BuildAdapterKind.Emscripten => new EmscriptenBuildAdapter(),
            BuildAdapterKind.Wasi => new WasiBuildAdapter(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(toolchain), toolchain.AdapterKind, null),
        };
}
