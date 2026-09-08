namespace Incant.CX.Arguments;

internal static class CompileArguments
{
    internal static void Generate(GenerationContext context)
    {
        CommonArguments.Target(context);
        CommonArguments.CodeGeneration(context);
        Language language = context.Language ?? Language.Cpp;
        bool creatingPch = context.Pch?.Mode == PchMode.Create;
        if (context.Microsoft)
        {
            context.Reject(ArgumentField.Language, language is Language.ObjectiveC or Language.ObjectiveCpp);
            context.Add("/nologo", "/c", context.Cpp ? "/TP" : "/TC");
        }
        else
        {
            string name = language switch
            {
                Language.C => "c",
                Language.Cpp => "c++",
                Language.ObjectiveC => "objective-c",
                _ => "objective-c++",
            };
            context.Add("-c", "-x", name + (creatingPch ? "-header" : ""));
        }

        if (context.Standard is string standard)
        {
            bool valid = context.Cpp ? standard is "c++98" or "c++03" or "c++11" or "c++14" or "c++17"
                or "c++20" or "c++23" or "c++26" or "c++latest" or "gnu++98" or "gnu++03" or "gnu++11"
                or "gnu++14" or "gnu++17" or "gnu++20" or "gnu++23" or "gnu++26"
                : standard is "c89" or "c90" or "c99" or "c11" or "c17" or "c23" or "gnu89" or "gnu90"
                or "gnu99" or "gnu11" or "gnu17" or "gnu23";
            context.Reject(ArgumentField.Standard, !valid, "The standard must match the selected language.");
            context.Reject(ArgumentField.Standard, context.Dialect == Dialect.Msvc && standard is not ("c++14" or "c++17" or "c++20" or "c++23" or "c++latest" or "c11" or "c17"),
                "This standard has no Microsoft command-line spelling.");
            if (standard == "c++latest" && context.Dialect != Dialect.Msvc)
            {
                standard = "c++26";
            }
            else if (standard == "c++23" && context.Dialect == Dialect.Msvc)
            {
                context.MinimumVersion(ArgumentField.Standard, new Version(19, 43));
                standard = "c++23preview";
            }

            context.Add((context.Dialect == Dialect.ClangCl ? "/clang:-std=" : context.Microsoft ? "/std:" : "-std=") + standard);
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
        CommonArguments.Raw(context, RawPosition.BeforeInputs);
        string output = context.Required(ArgumentField.Output, context.Output);
        if (!context.Microsoft && creatingPch)
        {
            output = context.Pch!.Artifact;
        }

        context.Add(context.Microsoft ? "/Fo" + output : "-o");
        if (!context.Microsoft)
        {
            context.Add(output);
        }

        IReadOnlyList<string> inputs = context.Inputs ?? [];
        if (inputs.Count != 1)
        {
            context.Error(ArgumentField.Inputs, "A compile command with an explicit output requires exactly one input.");
        }

        foreach (string input in inputs)
        {
            // A per-file language operand protects Unix absolute paths without consuming trailing options.
            context.Add(context.Microsoft ? (context.Cpp ? "/Tp" : "/Tc") + input
                : input.StartsWith('-') || input.StartsWith('@') ? "./" + input : input);
        }
    }

    internal static void Preprocessor(GenerationContext context, bool resource)
    {
        bool microsoft = context.Microsoft || resource;
        IReadOnlyDictionary<string, string?> definitions = (context.Defines ?? new Dictionary<string, string?>());
        foreach (KeyValuePair<string, string?> definition in definitions)
        {
            bool valid = definition.Key.Length > 0
                && (char.IsLetter(definition.Key[0]) || definition.Key[0] == '_')
                && definition.Key.All(character => char.IsLetterOrDigit(character) || character == '_');
            context.Reject(ArgumentField.Defines, !valid, $"'{definition.Key}' is not a macro identifier.");
            context.Add((resource ? "/d" : microsoft ? "/D" : "-D") + definition.Key
                + (definition.Value is null ? "" : "=" + definition.Value));
        }

        foreach (string name in context.Undefines ?? [])
        {
            context.Add((microsoft ? "/U" : "-U") + name);
        }

        foreach (string include in context.Includes ?? [])
        {
            context.Add(microsoft ? "/I" + include : "-I");
            if (!microsoft)
            {
                context.Add(include);
            }
        }

        foreach (string include in context.SystemIncludes ?? [])
        {
            if (resource)
            {
                context.Add("/I" + include);
            }
            else if (context.Dialect == Dialect.Msvc)
            {
                context.MinimumVersion(ArgumentField.SystemIncludes, new Version(19, 29));
                context.Add("/external:I" + include, "/external:W0");
            }
            else if (context.Dialect == Dialect.ClangCl)
            {
                context.Add("/imsvc" + include);
            }
            else
            {
                context.Add("-isystem", include);
            }
        }

        foreach (string include in context.ForcedIncludes ?? [])
        {
            context.Reject(ArgumentField.ForcedIncludes, resource);
            if (microsoft)
            {
                context.Add("/FI" + include);
            }
            else
            {
                context.Add("-include", include);
            }
        }

        foreach (string directory in context.FrameworkDirectories ?? [])
        {
            context.Reject(ArgumentField.FrameworkDirectories, !context.Apple);
            context.Add("-F", directory);
        }
    }

    private static void Diagnostics(GenerationContext context)
    {
        if (context.Has(ArgumentField.Warnings) && (context.Warnings ?? WarningLevel.None) != WarningLevel.Default)
        {
            WarningLevel level = context.Warnings ?? WarningLevel.None;
            if (context.Microsoft)
            {
                context.Add(level switch
                {
                    WarningLevel.None => "/W0",
                    WarningLevel.All => "/W3",
                    WarningLevel.Everything => "/Wall",
                    _ => "/W4",
                });
            }
            else
            {
                context.Reject(ArgumentField.Warnings, level == WarningLevel.Everything && context.Dialect == Dialect.Gnu,
                    "GCC has no all-diagnostics mode equivalent to Clang -Weverything.");
                context.Add(level switch
                {
                    WarningLevel.None => "-w",
                    WarningLevel.Everything => "-Weverything",
                    _ => "-Wall",
                });
                if (level == WarningLevel.Extra)
                {
                    context.Add("-Wextra");
                }
            }
        }

        if (context.Has(ArgumentField.WarningsAsErrors))
        {
            context.Add(context.Microsoft
                ? (context.WarningsAsErrors ?? false) ? "/WX" : "/WX-"
                : (context.WarningsAsErrors ?? false) ? "-Werror" : "-Wno-error");
        }

        foreach (KeyValuePair<string, bool> warning in (context.WarningControls ?? new Dictionary<string, bool>()))
        {
            context.Add(context.Microsoft ? (warning.Value ? "/w1" : "/wd") + warning.Key
                : (warning.Value ? "-W" : "-Wno-") + warning.Key);
        }

        if (context.Has(ArgumentField.Debug))
        {
            DebugFormat debug = context.Debug ?? DebugFormat.None;
            if (context.Microsoft)
            {
                context.Reject(ArgumentField.Debug, context.Dialect == Dialect.ClangCl && debug == DebugFormat.ProgramDatabase,
                    "clang-cl emits CodeView in objects; request the PDB from the linker instead.");
                if (debug != DebugFormat.None)
                {
                    context.Add(debug == DebugFormat.Embedded ? "/Z7" : "/Zi");
                }
            }
            else
            {
                context.Reject(ArgumentField.Debug, debug == DebugFormat.ProgramDatabase);
                context.Add(debug == DebugFormat.None ? "-g0" : "-g");
            }
        }

        if (context.Pdb is string pdb)
        {
            context.Reject(ArgumentField.Pdb, context.Dialect != Dialect.Msvc,
                "A compiler PDB path is an MSVC feature; other frontends create the PDB while linking.");
            context.Add("/Fd" + pdb);
        }

        if (context.DynamicDebug ?? false)
        {
            context.Reject(ArgumentField.DynamicDebug, context.Dialect != Dialect.Msvc);
            FeatureValidation.DynamicDebug(context);
            context.Add("/dynamicdeopt");
            if (!context.Has(ArgumentField.Debug))
            {
                context.Add("/Z7");
            }
        }
    }

    private static void Machine(GenerationContext context)
    {
        if (context.Cpp && context.Has(ArgumentField.Rtti))
        {
            context.Add(context.Microsoft ? (context.Rtti ?? false) ? "/GR" : "/GR-"
                : (context.Rtti ?? false) ? "-frtti" : "-fno-rtti");
        }

        BooleanFlag(context, ArgumentField.PositionIndependent, context.PositionIndependent ?? false, "-fPIC", "-fno-PIC");
        BooleanFlag(context, ArgumentField.PositionIndependentExecutable, context.PositionIndependentExecutable ?? false, "-fPIE", "-fno-PIE");
        BooleanFlag(context, ArgumentField.FunctionSections, context.FunctionSections ?? false, "-ffunction-sections", "-fno-function-sections", "/Gy", "/Gy-");
        BooleanFlag(context, ArgumentField.DataSections, context.DataSections ?? false, "-fdata-sections", "-fno-data-sections", "/Gw", "/Gw-");
        BooleanFlag(context, ArgumentField.BigObject, context.BigObject ?? false, "", "", "/bigobj", "");
        BooleanFlag(context, ArgumentField.FullSourcePaths, context.FullSourcePaths ?? false, "", "", "/FC", "");
        BooleanFlag(context, ArgumentField.ConformingPreprocessor, context.ConformingPreprocessor ?? false, "", "", "/Zc:preprocessor", "/Zc:preprocessor-");
        if (context.Has(ArgumentField.Visibility))
        {
            context.Reject(ArgumentField.Visibility, context.Microsoft);
            context.Add("-fvisibility=" + (context.Visibility ?? Visibility.Default).ToString().ToLowerInvariant());
        }

        if (context.Cpu is string cpu)
        {
            context.Reject(ArgumentField.Cpu, context.Microsoft, "Use an instruction-set selection with Microsoft drivers.");
            TargetArchitecture architecture = context.Architecture ?? TargetArchitecture.Unknown;
            context.Reject(ArgumentField.Cpu, architecture == TargetArchitecture.Unknown && !context.Wasm,
                "CPU selection requires a declared architecture to choose the correct compiler option.");
            context.Add((architecture is TargetArchitecture.X86 or TargetArchitecture.X64 ? "-march=" : "-mcpu=") + cpu);
        }

        if (context.Has(ArgumentField.InstructionSet))
        {
            InstructionSet instruction = context.InstructionSet ?? InstructionSet.Sse;
            bool x86 = instruction is not (InstructionSet.Neon or InstructionSet.WasmSimd128);
            TargetArchitecture architecture = context.Architecture ?? TargetArchitecture.Unknown;
            context.Reject(ArgumentField.InstructionSet, x86 && (context.Wasm || architecture is TargetArchitecture.ARM or TargetArchitecture.ARM64),
                "An x86 instruction-set request cannot be translated to another architecture.");
            context.Reject(ArgumentField.InstructionSet, instruction == InstructionSet.WasmSimd128 && !context.Wasm);
            context.Reject(ArgumentField.InstructionSet, instruction == InstructionSet.Neon && architecture is not (TargetArchitecture.ARM or TargetArchitecture.ARM64));
            if (context.Microsoft)
            {
                context.Reject(ArgumentField.InstructionSet, instruction is not (InstructionSet.Sse or InstructionSet.Sse2 or InstructionSet.Avx or InstructionSet.Avx2 or InstructionSet.Avx512));
                context.Add("/arch:" + instruction.ToString().ToUpperInvariant());
            }
            else if (instruction != InstructionSet.Neon || architecture != TargetArchitecture.ARM64)
            {
                context.Add(instruction switch
                {
                    InstructionSet.Sse41 => "-msse4.1",
                    InstructionSet.Sse42 => "-msse4.2",
                    InstructionSet.Avx512 => "-mavx512f",
                    InstructionSet.Neon => "-mfpu=neon",
                    InstructionSet.WasmSimd128 => "-msimd128",
                    _ => "-m" + instruction.ToString().ToLowerInvariant(),
                });
            }
        }
    }

    private static void BooleanFlag(GenerationContext context, ArgumentField field, bool selected,
        string enabled, string disabled, string? microsoftEnabled = null, string? microsoftDisabled = null)
    {
        if (!context.Has(field))
        {
            return;
        }

        string? flag = context.Microsoft
            ? selected ? microsoftEnabled : microsoftDisabled
            : selected ? enabled : disabled;
        if (string.IsNullOrEmpty(flag))
        {
            context.Reject(field, selected);
        }
        else
        {
            context.Add(flag);
        }
    }

    private static void PrecompiledHeader(GenerationContext context)
    {
        if (context.Pch is not Pch pch)
        {
            return;
        }

        context.Reject(ArgumentField.Pch, string.IsNullOrWhiteSpace(pch.Artifact)
            || (context.Dialect is Dialect.Msvc or Dialect.Gnu || pch.Mode == PchMode.Create)
            && string.IsNullOrWhiteSpace(pch.Header), "PCH header and artifact paths are required.");
        if (context.Dialect == Dialect.ClangCl && pch.Mode == PchMode.Use && string.IsNullOrEmpty(pch.Header))
        {
            context.Add("/clang:-include-pch", "/clang:" + pch.Artifact);
        }
        else if (context.Microsoft)
        {
            context.Add((pch.Mode == PchMode.Create ? "/Yc" : "/Yu") + pch.Header, "/Fp" + pch.Artifact, "/FI" + pch.Header);
            if (pch.Mode == PchMode.Create)
            {
                context.Reject(ArgumentField.Pch, pch.ObjectOutput is null || pch.ObjectOutput != context.Output,
                    "MSVC PCH creation must declare its object output as the command output.");
            }
        }
        else if (pch.Mode == PchMode.Create)
        {
            context.Reject(ArgumentField.Pch, context.Output != pch.Artifact, "PCH creation must declare its PCH artifact as the command output.");
            context.Reject(ArgumentField.Pch, context.Dialect == Dialect.Gnu && pch.Artifact != pch.Header + ".gch",
                "GCC PCH output must be named <header>.gch.");
        }
        else if (pch.Mode == PchMode.Use)
        {
            if (context.Dialect == Dialect.Gnu)
            {
                context.Reject(ArgumentField.Pch, pch.Artifact != pch.Header + ".gch", "GCC discovers a PCH beside its header as <header>.gch.");
                context.Add("-include", pch.Header!);
            }
            else
            {
                context.Add("-include-pch", pch.Artifact);
            }
        }
    }

    private static void DependencyOutput(GenerationContext context)
    {
        if (context.Dependencies is not Dependencies dependencies)
        {
            return;
        }

        if (dependencies.Mode == DependencyMode.ShowIncludes)
        {
            context.Reject(ArgumentField.Dependencies, !context.Microsoft);
            context.Add("/showIncludes");
        }
        else if (dependencies.Mode == DependencyMode.SourceDependencies)
        {
            context.Reject(ArgumentField.Dependencies, context.Dialect != Dialect.Msvc);
            context.MinimumVersion(ArgumentField.Dependencies, new Version(19, 27));
            context.Add("/sourceDependencies", dependencies.Path!);
        }
        else
        {
            context.Reject(ArgumentField.Dependencies, context.Dialect == Dialect.Msvc);
            if (context.Dialect == Dialect.ClangCl)
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

        context.Reject(ArgumentField.Dependencies, dependencies.Mode != DependencyMode.ShowIncludes && string.IsNullOrWhiteSpace(dependencies.Path),
            "Dependency file output requires a path.");
    }
}

internal static class ResourceArguments
{
    internal static void Generate(GenerationContext context)
    {
        context.Reject(ArgumentField.Platform, (context.Platform ?? TargetPlatform.Unknown) != TargetPlatform.Windows,
            "RC and LLVM RC operate on Windows resource files.");
        context.Reject(ArgumentField.Language, !context.Microsoft, "Select MSVC for RC or clang-cl for LLVM RC syntax.");
        context.Add("/nologo");
        CompileArguments.Preprocessor(context, resource: true);
        context.Add("/fo", context.Required(ArgumentField.Output, context.Output));
        CommonArguments.Raw(context, RawPosition.BeforeInputs);
        IReadOnlyList<string> inputs = context.Inputs ?? [];
        context.Reject(ArgumentField.Inputs, inputs.Count != 1, "Resource compilation requires exactly one input.");
        context.Add(inputs.ToArray());
    }
}
