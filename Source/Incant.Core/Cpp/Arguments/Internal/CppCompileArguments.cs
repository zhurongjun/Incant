using Incant.Core.Arguments;
using static Incant.Core.Cpp.Arguments.CppArguments;

namespace Incant.Core.Cpp.Arguments;

internal static class CppCompileArguments
{
    internal static void Generate(CppGenerationContext context)
    {
        CppCommonArguments.Target(context);
        CppCommonArguments.CodeGeneration(context);
        CppLanguage language = context.Get(Language, CppLanguage.Cpp);
        bool creatingPch = context.Get(Pch)?.Mode == CppPchMode.Create;
        if (context.Microsoft)
        {
            context.Reject(Language, language is CppLanguage.ObjectiveC or CppLanguage.ObjectiveCpp);
            context.Add("/nologo", "/c", context.Cpp ? "/TP" : "/TC");
        }
        else
        {
            string name = language switch
            {
                CppLanguage.C => "c",
                CppLanguage.Cpp => "c++",
                CppLanguage.ObjectiveC => "objective-c",
                _ => "objective-c++",
            };
            context.Add("-c", "-x", name + (creatingPch ? "-header" : ""));
        }

        if (context.Get(Standard) is string standard)
        {
            bool valid = context.Cpp ? standard is "c++98" or "c++03" or "c++11" or "c++14" or "c++17"
                or "c++20" or "c++23" or "c++26" or "c++latest" or "gnu++98" or "gnu++03" or "gnu++11"
                or "gnu++14" or "gnu++17" or "gnu++20" or "gnu++23" or "gnu++26"
                : standard is "c89" or "c90" or "c99" or "c11" or "c17" or "c23" or "gnu89" or "gnu90"
                or "gnu99" or "gnu11" or "gnu17" or "gnu23";
            context.Reject(Standard, !valid, "The standard must match the selected language.");
            context.Reject(Standard, context.Dialect == CppDialect.Msvc && standard is not ("c++14" or "c++17" or "c++20" or "c++23" or "c++latest" or "c11" or "c17"),
                "This standard has no Microsoft command-line spelling.");
            if (standard == "c++latest" && context.Dialect != CppDialect.Msvc)
            {
                standard = "c++26";
            }
            else if (standard == "c++23" && context.Dialect == CppDialect.Msvc)
            {
                context.MinimumVersion(Standard, new Version(19, 43));
                standard = "c++23preview";
            }

            context.Add((context.Dialect == CppDialect.ClangCl ? "/clang:-std=" : context.Microsoft ? "/std:" : "-std=") + standard);
            if (context.Microsoft && context.Cpp)
            {
                context.Add("/Zc:__cplusplus");
            }
        }

        Preprocessor(context, resource: false);
        Diagnostics(context);
        Machine(context);
        PrecompiledHeader(context);
        DependencyOutput(context);
        CppCommonArguments.Raw(context, CppRawPosition.BeforeInputs);
        string output = context.Required(Output);
        if (!context.Microsoft && creatingPch)
        {
            output = context.Get(Pch)!.Artifact;
        }

        context.Add(context.Microsoft ? "/Fo" + output : "-o");
        if (!context.Microsoft)
        {
            context.Add(output);
        }

        IReadOnlyList<string> inputs = context.List(Inputs);
        if (inputs.Count != 1)
        {
            context.Error(Inputs, "A compile command with an explicit output requires exactly one input.");
        }

        foreach (string input in inputs)
        {
            // A per-file language operand protects Unix absolute paths without consuming trailing options.
            context.Add(context.Microsoft ? (context.Cpp ? "/Tp" : "/Tc") + input
                : input.StartsWith('-') || input.StartsWith('@') ? "./" + input : input);
        }
    }

    internal static void Preprocessor(CppGenerationContext context, bool resource)
    {
        bool microsoft = context.Microsoft || resource;
        IReadOnlyDictionary<string, string?> definitions = context.Get(Defines,
            new Dictionary<string, string?>());
        foreach (KeyValuePair<string, string?> definition in definitions)
        {
            bool valid = definition.Key.Length > 0
                && (char.IsLetter(definition.Key[0]) || definition.Key[0] == '_')
                && definition.Key.All(character => char.IsLetterOrDigit(character) || character == '_');
            context.Reject(Defines, !valid, $"'{definition.Key}' is not a macro identifier.");
            context.Add((resource ? "/d" : microsoft ? "/D" : "-D") + definition.Key
                + (definition.Value is null ? "" : "=" + definition.Value));
        }

        foreach (string name in context.List(Undefines))
        {
            context.Add((microsoft ? "/U" : "-U") + name);
        }

        foreach (string include in context.List(Includes))
        {
            context.Add(microsoft ? "/I" + include : "-I");
            if (!microsoft)
            {
                context.Add(include);
            }
        }

        foreach (string include in context.List(SystemIncludes))
        {
            if (resource)
            {
                context.Add("/I" + include);
            }
            else if (context.Dialect == CppDialect.Msvc)
            {
                context.MinimumVersion(SystemIncludes, new Version(19, 29));
                context.Add("/external:I" + include, "/external:W0");
            }
            else if (context.Dialect == CppDialect.ClangCl)
            {
                context.Add("/imsvc" + include);
            }
            else
            {
                context.Add("-isystem", include);
            }
        }

        foreach (string include in context.List(ForcedIncludes))
        {
            context.Reject(ForcedIncludes, resource);
            if (microsoft)
            {
                context.Add("/FI" + include);
            }
            else
            {
                context.Add("-include", include);
            }
        }

        foreach (string directory in context.List(FrameworkDirectories))
        {
            context.Reject(FrameworkDirectories, !context.Apple);
            context.Add("-F", directory);
        }
    }

    private static void Diagnostics(CppGenerationContext context)
    {
        if (context.Has(Warnings) && context.Get(Warnings) != CppWarningLevel.Default)
        {
            CppWarningLevel level = context.Get(Warnings);
            if (context.Microsoft)
            {
                context.Add(level switch
                {
                    CppWarningLevel.None => "/W0",
                    CppWarningLevel.All => "/W3",
                    CppWarningLevel.Everything => "/Wall",
                    _ => "/W4",
                });
            }
            else
            {
                context.Reject(Warnings, level == CppWarningLevel.Everything && context.Dialect == CppDialect.Gnu,
                    "GCC has no all-diagnostics mode equivalent to Clang -Weverything.");
                context.Add(level switch
                {
                    CppWarningLevel.None => "-w",
                    CppWarningLevel.Everything => "-Weverything",
                    _ => "-Wall",
                });
                if (level == CppWarningLevel.Extra)
                {
                    context.Add("-Wextra");
                }
            }
        }

        if (context.Has(WarningsAsErrors))
        {
            context.Add(context.Microsoft
                ? context.Get(WarningsAsErrors) ? "/WX" : "/WX-"
                : context.Get(WarningsAsErrors) ? "-Werror" : "-Wno-error");
        }

        foreach (KeyValuePair<string, bool> warning in context.Get(WarningControls, new Dictionary<string, bool>()))
        {
            context.Add(context.Microsoft ? (warning.Value ? "/w1" : "/wd") + warning.Key
                : (warning.Value ? "-W" : "-Wno-") + warning.Key);
        }

        if (context.Has(Debug))
        {
            CppDebugFormat debug = context.Get(Debug);
            if (context.Microsoft)
            {
                context.Reject(Debug, context.Dialect == CppDialect.ClangCl && debug == CppDebugFormat.ProgramDatabase,
                    "clang-cl emits CodeView in objects; request the PDB from the linker instead.");
                if (debug != CppDebugFormat.None)
                {
                    context.Add(debug == CppDebugFormat.Embedded ? "/Z7" : "/Zi");
                }
            }
            else
            {
                context.Reject(Debug, debug == CppDebugFormat.ProgramDatabase);
                context.Add(debug == CppDebugFormat.None ? "-g0" : "-g");
            }
        }

        if (context.Get(Pdb) is string pdb)
        {
            context.Reject(Pdb, context.Dialect != CppDialect.Msvc,
                "A compiler PDB path is an MSVC feature; other frontends create the PDB while linking.");
            context.Add("/Fd" + pdb);
        }

        if (context.Get(DynamicDebug))
        {
            context.Reject(DynamicDebug, context.Dialect != CppDialect.Msvc);
            CppFeatureValidation.DynamicDebug(context);
            context.Add("/dynamicdeopt");
            if (!context.Has(Debug))
            {
                context.Add("/Z7");
            }
        }
    }

    private static void Machine(CppGenerationContext context)
    {
        if (context.Cpp && context.Has(Rtti))
        {
            context.Add(context.Microsoft ? context.Get(Rtti) ? "/GR" : "/GR-"
                : context.Get(Rtti) ? "-frtti" : "-fno-rtti");
        }

        BooleanFlag(context, PositionIndependent, "-fPIC", "-fno-PIC");
        BooleanFlag(context, PositionIndependentExecutable, "-fPIE", "-fno-PIE");
        BooleanFlag(context, FunctionSections, "-ffunction-sections", "-fno-function-sections", "/Gy", "/Gy-");
        BooleanFlag(context, DataSections, "-fdata-sections", "-fno-data-sections", "/Gw", "/Gw-");
        BooleanFlag(context, BigObject, "", "", "/bigobj", "");
        BooleanFlag(context, FullSourcePaths, "", "", "/FC", "");
        BooleanFlag(context, ConformingPreprocessor, "", "", "/Zc:preprocessor", "/Zc:preprocessor-");
        if (context.Has(Visibility))
        {
            context.Reject(Visibility, context.Microsoft);
            context.Add("-fvisibility=" + context.Get(Visibility).ToString().ToLowerInvariant());
        }

        if (context.Get(Cpu) is string cpu)
        {
            context.Reject(Cpu, context.Microsoft, "Use an instruction-set selection with Microsoft drivers.");
            TargetArchitecture architecture = context.Get(Architecture);
            context.Reject(Cpu, architecture == TargetArchitecture.Unknown && !context.Wasm,
                "CPU selection requires a declared architecture to choose the correct compiler option.");
            context.Add((architecture is TargetArchitecture.X86 or TargetArchitecture.X64 ? "-march=" : "-mcpu=") + cpu);
        }

        if (context.Has(InstructionSet))
        {
            CppInstructionSet instruction = context.Get(InstructionSet);
            bool x86 = instruction is not (CppInstructionSet.Neon or CppInstructionSet.WasmSimd128);
            TargetArchitecture architecture = context.Get(Architecture);
            context.Reject(InstructionSet, x86 && (context.Wasm || architecture is TargetArchitecture.ARM or TargetArchitecture.ARM64),
                "An x86 instruction-set request cannot be translated to another architecture.");
            context.Reject(InstructionSet, instruction == CppInstructionSet.WasmSimd128 && !context.Wasm);
            context.Reject(InstructionSet, instruction == CppInstructionSet.Neon && architecture is not (TargetArchitecture.ARM or TargetArchitecture.ARM64));
            if (context.Microsoft)
            {
                context.Reject(InstructionSet, instruction is not (CppInstructionSet.Sse or CppInstructionSet.Sse2 or CppInstructionSet.Avx or CppInstructionSet.Avx2 or CppInstructionSet.Avx512));
                context.Add("/arch:" + instruction.ToString().ToUpperInvariant());
            }
            else if (instruction != CppInstructionSet.Neon || architecture != TargetArchitecture.ARM64)
            {
                context.Add(instruction switch
                {
                    CppInstructionSet.Sse41 => "-msse4.1",
                    CppInstructionSet.Sse42 => "-msse4.2",
                    CppInstructionSet.Avx512 => "-mavx512f",
                    CppInstructionSet.Neon => "-mfpu=neon",
                    CppInstructionSet.WasmSimd128 => "-msimd128",
                    _ => "-m" + instruction.ToString().ToLowerInvariant(),
                });
            }
        }
    }

    private static void BooleanFlag(CppGenerationContext context, ArgumentKey<bool> key,
        string enabled, string disabled, string? microsoftEnabled = null, string? microsoftDisabled = null)
    {
        if (!context.Has(key))
        {
            return;
        }

        string? flag = context.Microsoft
            ? context.Get(key) ? microsoftEnabled : microsoftDisabled
            : context.Get(key) ? enabled : disabled;
        if (string.IsNullOrEmpty(flag))
        {
            context.Reject(key, context.Get(key));
        }
        else
        {
            context.Add(flag);
        }
    }

    private static void PrecompiledHeader(CppGenerationContext context)
    {
        if (context.Get(Pch) is not CppPch pch)
        {
            return;
        }

        context.Reject(Pch, string.IsNullOrWhiteSpace(pch.Artifact)
            || (context.Dialect is CppDialect.Msvc or CppDialect.Gnu || pch.Mode == CppPchMode.Create)
            && string.IsNullOrWhiteSpace(pch.Header),
            "PCH header and artifact paths are required.");
        if (context.Dialect == CppDialect.ClangCl && pch.Mode == CppPchMode.Use && string.IsNullOrEmpty(pch.Header))
        {
            context.Add("/clang:-include-pch", "/clang:" + pch.Artifact);
        }
        else if (context.Microsoft)
        {
            context.Add((pch.Mode == CppPchMode.Create ? "/Yc" : "/Yu") + pch.Header, "/Fp" + pch.Artifact, "/FI" + pch.Header);
            if (pch.Mode == CppPchMode.Create)
            {
                context.Reject(Pch, pch.ObjectOutput is null || pch.ObjectOutput != context.Get(Output),
                    "MSVC PCH creation must declare its object output as the command output.");
            }
        }
        else if (pch.Mode == CppPchMode.Create)
        {
            context.Reject(Pch, context.Get(Output) != pch.Artifact,
                "PCH creation must declare its PCH artifact as the command output.");
            context.Reject(Pch, context.Dialect == CppDialect.Gnu && pch.Artifact != pch.Header + ".gch",
                "GCC PCH output must be named <header>.gch.");
        }
        else if (pch.Mode == CppPchMode.Use)
        {
            if (context.Dialect == CppDialect.Gnu)
            {
                context.Reject(Pch, pch.Artifact != pch.Header + ".gch",
                    "GCC discovers a PCH beside its header as <header>.gch.");
                context.Add("-include", pch.Header!);
            }
            else
            {
                context.Add("-include-pch", pch.Artifact);
            }
        }
    }

    private static void DependencyOutput(CppGenerationContext context)
    {
        if (context.Get(Dependencies) is not CppDependencies dependencies)
        {
            return;
        }

        if (dependencies.Mode == CppDependencyMode.ShowIncludes)
        {
            context.Reject(Dependencies, !context.Microsoft);
            context.Add("/showIncludes");
        }
        else if (dependencies.Mode == CppDependencyMode.SourceDependencies)
        {
            context.Reject(Dependencies, context.Dialect != CppDialect.Msvc);
            context.MinimumVersion(Dependencies, new Version(19, 27));
            context.Add("/sourceDependencies", dependencies.Path!);
        }
        else
        {
            context.Reject(Dependencies, context.Dialect == CppDialect.Msvc);
            if (context.Dialect == CppDialect.ClangCl)
            {
                context.Add("/clang:-MD", "/clang:-MF", "/clang:" + dependencies.Path);
            }
            else
            {
                context.Add("-MD", "-MF", dependencies.Path!);
                if (dependencies.ObjectName is string objectName)
                {
                    context.Add("-MT", objectName);
                }
            }
        }

        context.Reject(Dependencies, dependencies.Mode != CppDependencyMode.ShowIncludes && string.IsNullOrWhiteSpace(dependencies.Path),
            "Dependency file output requires a path.");
    }
}

internal static class CppResourceArguments
{
    internal static void Generate(CppGenerationContext context)
    {
        context.Reject(Platform, context.Get(Platform) != TargetPlatform.Windows,
            "RC and LLVM RC operate on Windows resource files.");
        context.Reject(Language, !context.Microsoft,
            "Select MSVC for RC or clang-cl for LLVM RC syntax.");
        context.Add("/nologo");
        CppCompileArguments.Preprocessor(context, resource: true);
        context.Add("/fo", context.Required(Output));
        CppCommonArguments.Raw(context, CppRawPosition.BeforeInputs);
        IReadOnlyList<string> inputs = context.List(Inputs);
        context.Reject(Inputs, inputs.Count != 1, "Resource compilation requires exactly one input.");
        context.Add(inputs.ToArray());
    }
}
