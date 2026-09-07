namespace Incant.AutoTest.CppToolchain.Setup;

internal sealed record AndroidRelease(
    string Id,
    string Version,
    string Release,
    string Sha256);

internal sealed record EmscriptenRelease(
    string Version,
    string ReleaseRevision,
    string Sha256);

internal sealed record EmscriptenHostPackage(
    string Key,
    string ReleasePlatform,
    string ReleaseFile,
    string NodeFile,
    string NodeSha256,
    string? PythonFile,
    string? PythonSha256);

internal sealed record WasiRelease(
    int Version,
    string Platform,
    string Sha256,
    IReadOnlyList<string> RequiredFiles);

internal sealed record WasmtimeRelease(
    string Version,
    string Platform,
    string Extension,
    string Sha256,
    string Executable);

internal static class BundleCatalog
{
    // These files describe the pinned dual-exception WASIp1 distributions, independently of Finder.
    private static readonly IReadOnlyList<string> s_wasiPreview1Files = Array.AsReadOnly(new[]
    {
        "include/wasm32-wasip1/stdio.h",
        "include/wasm32-wasip1/noeh/c++/v1/array",
        "include/wasm32-wasip1/eh/c++/v1/array",
        "lib/wasm32-wasip1/libc.a",
        "lib/wasm32-wasip1/crt1-command.o",
        "lib/wasm32-wasip1/noeh/libc++.a",
        "lib/wasm32-wasip1/noeh/libc++abi.a",
        "lib/wasm32-wasip1/eh/libc++.a",
        "lib/wasm32-wasip1/eh/libc++abi.a",
        "lib/wasm32-wasip1/eh/libunwind.a",
    });

    internal const string EmsdkRevision = "5eb0bde7585670252e8ba05e9d361627bffd08b5";
    internal const string EmsdkUri = "https://github.com/emscripten-core/emsdk.git";
    internal const string EmscriptenPackageRoot =
        "https://storage.googleapis.com/webassembly/emscripten-releases-builds";

    internal static IReadOnlyList<AndroidRelease> Android { get; } =
    [
        new(
            "android-25.2.9519653",
            "25.2.9519653",
            "r25c",
            HostValue(
                "f70093964f6cbbe19268f9876a20f92d3a593db3ad2037baadd25fd8d71e84e2",
                "769ee342ea75f80619d985c2da990c48b3d8eaf45f48783a2d48870d04b46108",
                "b01bae969a5d0bfa0da18469f650a1628dc388672f30e0ba231da5c74245bc92")),
        new(
            "android-27.2.12479018",
            "27.2.12479018",
            "r27c",
            HostValue(
                "27e49f11e0cee5800983d8af8f4acd5bf09987aa6f790d4439dda9f3643d2494",
                "59c2f6dc96743b5daf5d1626684640b20a6bd2b1d85b13156b90333741bad5cc",
                "8c5685457c58a88527367d46d3f14e8c727d962c39f85344cff0c0768a73c3b7")),
    ];

    internal static IReadOnlyList<EmscriptenRelease> Emscripten { get; } =
    [
        new(
            "3.1.64",
            "fd61bacaf40131f74987e649a135f1dd559aff60",
            HostValue(
                "eb5b59afb420915daab4c383e5f73d456cc14776dce02fdc852c46522cda5531",
                "c39de24beca60fd580f6dff0eca0e275016042a30234588b19eda82397e299f3",
                "47449057c345a09aa8750be1a357c364ffea9f8a066066cb341a7a2a14bac96a")),
        new(
            "6.0.9",
            "f04ea239d533260dd1db760dd2d668d5f9a88d6b",
            HostValue(
                "f7512eab6e69ad9d7de5adbf39e68d7d6773b317b13e70ec5003ef1d10f92980",
                "d5c6c2917fbc1cae1a7d1e581f1c0b2817369dd57f94c7a0d05921476f1a7287",
                "b60514308507f64f4138d3c55bdb6979f20222288700fde603dced23b65dd533")),
    ];

    internal static EmscriptenHostPackage EmscriptenHost => OperatingSystem.IsWindows()
        ? new EmscriptenHostPackage(
            "windows",
            "win",
            "wasm-binaries.zip",
            "node-v24.19.0-win-x64.zip",
            "57f71ab3652e797d84acddc79c81cc9ff1c6ddb2a1974cdb83f00fee9bff4c73",
            "python-3.13.3-0-win-amd64.zip",
            "6fe7a540c6b8b185780467cf7495e884ee62316ec4abb19e1c735b8a77c62465")
        : OperatingSystem.IsLinux()
            ? new EmscriptenHostPackage(
                "linux",
                "linux",
                "wasm-binaries.tar.xz",
                "node-v24.19.0-linux-x64.tar.xz",
                "14b342e71204f811bde6153be8e04b62aef63c236fef92b55f9c83154b409647",
                null,
                null)
            : OperatingSystem.IsMacOS()
                ? new EmscriptenHostPackage(
                    "macos-arm64",
                    "mac",
                    "wasm-binaries-arm64.tar.xz",
                    "node-v24.19.0-darwin-arm64.tar.gz",
                    "8294b7aa9b03997481c06babf1e8b270c859358f27da57a11509afe537ac381d",
                    "python-3.13.3-0-macos-arm64.tar.gz",
                    "2b0899d7ade9463b0c909ef23aeea27546a4f412637998d817a49718bc2bac19")
                : throw new PlatformNotSupportedException(
                    "Emscripten provisioning supports Windows, Linux, and macOS.");

    internal static IReadOnlyList<WasiRelease> Wasi { get; } =
    [
        new(
            33,
            HostValue("x86_64-windows", "x86_64-linux", "arm64-macos"),
            HostValue(
                "df14ca2a2127c2d6b6be07e6f5549b3af9c1b3c0112430c200a4749970c59f06",
                "0ba8b5bfaeb2adf3f29bab5841d76cf5318ab8e1642ea195f88baba1abd47bce",
                "85c997a2665ead91673b5bb88b7d0df3fc8900df3bfa244f720d478187bbdc78"),
            s_wasiPreview1Files),
        new(
            34,
            HostValue("x86_64-windows", "x86_64-linux", "arm64-macos"),
            HostValue(
                "cccb5c323a9b34f0349a9b09e8804a0a7632c68c3310f4b5f437ed57d7e71d8f",
                "b761e3a0721dbae9c09a0059e5fdb2bf917d1b4a8a7b430fb3b5aafb0984b2c4",
                "9c59398106b417f8f14913380fdf0097a8cc0ff4af9eb3ce0065a859e88d49e9"),
            s_wasiPreview1Files),
    ];

    internal static WasmtimeRelease Wasmtime => OperatingSystem.IsWindows()
        ? new WasmtimeRelease(
            "45.0.0",
            "x86_64-windows",
            "zip",
            "edb9572c6e8ae7c51053af826a8bc85bf205a759c9e83ddb08a941b26e297706",
            "wasmtime.exe")
        : OperatingSystem.IsLinux()
            ? new WasmtimeRelease(
                "45.0.0",
                "x86_64-linux",
                "tar.xz",
                "9d92e6dc04630f617e0e5d532327a5a917ac4898587e07f4fb7a5fc7fffef760",
                "wasmtime")
            : OperatingSystem.IsMacOS()
                ? new WasmtimeRelease(
                    "45.0.0",
                    "aarch64-macos",
                    "tar.xz",
                    "8c589a1feb6578ddfd76d4ee07bac551d7f3069d6cef9b2ae5e87e630b5198db",
                    "wasmtime")
                : throw new PlatformNotSupportedException(
                    "Wasmtime provisioning supports Windows, Linux, and macOS.");

    private static string HostValue(string windows, string linux, string macOS)
    {
        if (OperatingSystem.IsWindows())
        {
            return windows;
        }

        if (OperatingSystem.IsLinux())
        {
            return linux;
        }

        if (OperatingSystem.IsMacOS())
        {
            return macOS;
        }

        throw new PlatformNotSupportedException(
            "Toolchain bundle provisioning supports Windows, Linux, and macOS.");
    }
}
