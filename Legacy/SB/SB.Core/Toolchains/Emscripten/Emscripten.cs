using Serilog;

namespace SB.Core
{
    using BS = BuildInstance;

    public partial class Emscripten : IToolchain
    {
        public static void RegisterSetups(BuildInstance instance)
        {
            instance.AddSetup<EmscriptenSetup>();
        }

        public string Name => "emscripten";
        public Version Version => EmccVersion!;
        public ICompiler Compiler => Emcc!;
        public ILinker Linker => Emcc!;
        public IArchiver Archiver => Emar!;

        // --- Resolved paths (filled by EmscriptenSetup) ---
        public string? EmsdkRoot { get; internal set; }
        public string? EmscriptenRoot { get; internal set; }    // <EmsdkRoot>/upstream/emscripten
        public string? EmccPath { get; internal set; }          // emcc[.bat]
        public string? EmppPath { get; internal set; }          // em++[.bat]
        public string? EmarPath { get; internal set; }          // emar[.bat]
        public string? EmranlibPath { get; internal set; }      // emranlib[.bat]
        public string? NodePath { get; internal set; }          // EMSDK_NODE, optional
        public string? PythonPath { get; internal set; }        // EMSDK_PYTHON, optional
        public Version? EmccVersion { get; internal set; }

        // --- Tool instances (filled by EmscriptenSetup after path resolution) ---
        public EmccCompiler? Emcc { get; internal set; }
        public EmarArchiver? Emar { get; internal set; }
    }
}
