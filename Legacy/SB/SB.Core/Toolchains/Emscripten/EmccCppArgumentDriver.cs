namespace SB.Core
{
    using ArgumentName = string;
    using BS = BuildInstance;

    /// <summary>
    /// Maps target properties to emcc / em++ compile flags. emcc is a clang
    /// frontend so the flag surface is mostly shared with AppleClang; this
    /// driver diverges only where wasm semantics differ (no sysroot raw args,
    /// no x86/arm SIMD, wasm-specific opt defaults).
    /// </summary>
    [ArgumentDriver(InjectType = typeof(CppTarget))]
    public class EmccCppArgumentDriver : IArgumentDriver
    {
        public EmccCppArgumentDriver(BuildInstance instance, Emscripten toolchain, CFamily language, bool isPCH)
        {
            this.Instance = instance;
            this.Toolchain = toolchain;
            this.Language = language;
            this.isPCH = isPCH;
            if (instance.EmscriptenThreads)
                RawArguments.Add("-pthread");
        }

        [TargetProperty]
        public string Exception(bool Enable) => Enable ? "-fwasm-exceptions" : "-fno-exceptions";

        [TargetProperty]
        public string RTTI(bool v) => v ? "-frtti" : "-fno-rtti";

        [TargetProperty]
        public string CppVersion(string what) =>
            Language == CFamily.Cpp || Language == CFamily.ObjCpp ?
            (AppleClangArgumentDriver.cppVersionMap.TryGetValue(what.Replace("c++", "").Replace("C++", ""), out var r) ? r : throw new TaskFatalError($"Invalid argument \"{what}\" for CppVersion!")) :
            "";

        [TargetProperty]
        public string CVersion(string what) =>
            Language == CFamily.C || Language == CFamily.ObjC ?
            (AppleClangArgumentDriver.cVersionMap.TryGetValue(what.Replace("c", "").Replace("C", ""), out var r) ? r : throw new TaskFatalError($"Invalid argument \"{what}\" for CVersion!")) :
            "";

        [TargetProperty]
        public string SIMD(SIMDArchitecture simd) => simd switch
        {
            // On wasm only SIMD128 is meaningful. Host-native SIMD enums
            // silently collapse to wasm simd128 so shared target declarations
            // (which already specify SSE/AVX/Neon for native) keep compiling.
            SIMDArchitecture.Wasm128 => "-msimd128",
            SIMDArchitecture.SSE2 => "-msimd128",
            SIMDArchitecture.SSE4_2 => "-msimd128",
            SIMDArchitecture.AVX => "-msimd128",
            SIMDArchitecture.AVX512 => "-msimd128",
            SIMDArchitecture.AVX10_1 => "-msimd128",
            SIMDArchitecture.Neon => "-msimd128",
            _ => ""
        };

        [TargetProperty]
        public string WarningAsError(bool Enable) => Enable ? "-Werror" : "-Wno-error";

        [TargetProperty]
        public string[] OptimizationLevel(OptimizationLevel level) => level switch
        {
            // Keep the generic names honest: Fastest favors runtime speed;
            // Smallest remains the size-oriented mode.
            SB.Core.OptimizationLevel.None => ["-O0"],
            SB.Core.OptimizationLevel.Fast => ["-O1"],
            SB.Core.OptimizationLevel.Faster => ["-O2"],
            SB.Core.OptimizationLevel.Fastest => ["-O3", "-flto"],
            SB.Core.OptimizationLevel.Smallest => ["-Oz", "-flto"],
            _ => throw new TaskFatalError($"Invalid argument \"{level}\" for OptimizationLevel!")
        };

        [TargetProperty]
        public string FpModel(FpModel v) => $"-ffp-model={v}".ToLowerInvariant();

        [TargetProperty]
        public virtual string DebugSymbols(bool Enable) => Enable ? "-g" : "";

        [TargetProperty(InheritBehavior = true)]
        public virtual string[] CppFlags(ArgumentList<string> flags) => (Language == CFamily.Cpp) ? CXFlags(flags) : new string[0];

        [TargetProperty(InheritBehavior = true)]
        public virtual string[] CFlags(ArgumentList<string> flags) => (Language == CFamily.C) ? CXFlags(flags) : new string[0];

        [TargetProperty(InheritBehavior = true)]
        public virtual string[] CXFlags(ArgumentList<string> flags) => flags.Select(flag => flag).ToArray();

        [TargetProperty(InheritBehavior = true)]
        public string[] Defines(ArgumentList<string> defines) => defines.Select(define => $"-D{define}").ToArray();

        [TargetProperty(InheritBehavior = true, PathBehavior = true)]
        public string[]? IncludeDirs(ArgumentList<string> dirs) => dirs.All(x => BS.CheckPath(x, true) ? true : throw new TaskFatalError($"Invalid include dir {x}!")) ? dirs.Select(dir => $"-I{dir}").ToArray() : null;

        // emcc shares the clang flag surface; Clang_* passthroughs keep
        // cross-toolchain target definitions reusable.
        [TargetProperty(InheritBehavior = true)]
        public virtual string[] Clang_CppFlags(ArgumentList<string> flags) => CppFlags(flags);

        [TargetProperty(InheritBehavior = true)]
        public virtual string[] Clang_CFlags(ArgumentList<string> flags) => CFlags(flags);

        [TargetProperty(InheritBehavior = true)]
        public virtual string[] Clang_CXFlags(ArgumentList<string> flags) => CXFlags(flags);

        // Emscripten-specific escape hatch for -s<KEY>=<VAL> and other flags
        // that are only meaningful when building for wasm. Fluent setters for
        // these are auto-generated by SB.SourceGenerator from the [TargetProperty]
        // attribute so build.cs can write `.Emscripten_CXFlags(Visibility.Private, "-sDISABLE_EXCEPTION_CATCHING=1")`.
        [TargetProperty(InheritBehavior = true)]
        public virtual string[] Emscripten_CppFlags(ArgumentList<string> flags) => (Language == CFamily.Cpp) ? flags.ToArray() : new string[0];

        [TargetProperty(InheritBehavior = true)]
        public virtual string[] Emscripten_CFlags(ArgumentList<string> flags) => (Language == CFamily.C) ? flags.ToArray() : new string[0];

        [TargetProperty(InheritBehavior = true)]
        public virtual string[] Emscripten_CXFlags(ArgumentList<string> flags) => flags.ToArray();

        // --- Non-[TargetProperty] slots populated by CppCompileEmitter ---

        public string[] Source(string path) => BS.CheckFile(path, true) ? GetLanguageArgString($" \"{path}\"") : throw new TaskFatalError($"Source value {path} is not an existed absolute path!");

        public string Object(string path) => BS.CheckFile(path, false) ? $"-o\"{path}\"" : throw new TaskFatalError($"Object value {path} is not a valid absolute path!");

        public string[] SourceDependencies(string path) => BS.CheckFile(path, false) ? new string[] { "-MD", "-MF", $"\"{path}\"" } : throw new TaskFatalError($"SourceDependencies value {path} is not a valid absolute path!");

        public string UsePCHAST(string path) => BS.CheckFile(path, false) ? $"-include-pch \"{path}\"" : throw new TaskFatalError($"PCHObject value {path} is not a valid absolute path!");

        // --- No-ops: wasm has no host arch, PDB, or separate debug store ---

        public string Arch(Architecture arch) => "";
        public string PDBMode(PDBMode mode) => "";
        public string PDB(string path) => "";

        // --- Helpers ---

        protected string[] GetLanguageArgString(string p)
        {
            string lang;
            switch (Language)
            {
                case CFamily.C:
                    lang = isPCH ? "c-header" : "";
                    break;
                case CFamily.Cpp:
                    lang = isPCH ? "c++-header" : "c++";
                    break;
                case CFamily.ObjC:
                    lang = isPCH ? "objectivec-header" : "objective-c";
                    break;
                case CFamily.ObjCpp:
                    lang = isPCH ? "objectivec++-header" : "objective-c++";
                    break;
                default:
                    throw new TaskFatalError($"Invalid language \"{Language}\" for emcc!");
            }
            return string.IsNullOrEmpty(lang) ? new string[] { p } : new string[] { $"-x", lang, p };
        }

        protected BuildInstance Instance { get; }
        protected Emscripten Toolchain { get; }
        protected CFamily Language { get; }
        protected bool isPCH = false;

        public ArgumentDictionary Arguments { get; } = new ArgumentDictionary();
        public HashSet<string> RawArguments { get; } = new HashSet<string>
        {
            "-c",
            // Expose POSIX.1-2008 + GNU extensions unconditionally. Engine code
            // uses clock_gettime / nanosleep / posix_memalign / pthread_*_np
            // which all sit behind feature-test guards in emscripten's sysroot.
            // Without these defines every TU that transitively pulls in
            // thread.inl / memory.c / linux_time.c fails to compile under -std=c11.
            "-D_POSIX_C_SOURCE=200809L",
            "-D_GNU_SOURCE"
        };
    }
}
