using static Incant.Core.Cpp.Arguments.CppArguments;

namespace Incant.Core.Cpp.Arguments;

internal static class CppLinkArguments
{
    internal static void Generate(CppGenerationContext context)
    {
        bool microsoft = context.Get(LinkerDialect) is CppLinkerDialect.Msvc or CppLinkerDialect.LldLink;
        if (microsoft)
        {
            Microsoft(context);
        }
        else
        {
            context.Reject(LinkerDialect, context.Microsoft,
                "A Microsoft-style frontend requires an explicitly selected LINK or lld-link interpreter.");
            CppCommonArguments.Target(context);
            CppCommonArguments.CodeGeneration(context);
            Driver(context);
        }

        foreach (string directory in context.List(LibraryDirectories))
        {
            if (microsoft)
            {
                context.Add("/LIBPATH:" + directory);
            }
            else
            {
                context.Add("-L", directory);
            }
        }

        CppCommonArguments.Raw(context, CppRawPosition.BeforeInputs);
        context.Add(context.List(Inputs).ToArray());
        foreach (CppLinkInput input in context.List(LinkInputs))
        {
            Input(context, input, microsoft);
        }

        foreach (CppLinkInput runtime in context.List(RuntimeLibraries))
        {
            Input(context, runtime, microsoft);
        }

        if (context.List(Inputs).Count == 0 && context.List(LinkInputs).Count == 0)
        {
            context.Error(Inputs, "A link command requires inputs.");
        }

        string output = context.Required(Output);
        if (microsoft)
        {
            context.Add("/OUT:" + output);
        }
        else
        {
            context.Add("-o", output);
        }
    }

    private static void Microsoft(CppGenerationContext context)
    {
        context.Add("/NOLOGO");
        string? machine = context.Get(Architecture) switch
        {
            TargetArchitecture.X86 => "X86",
            TargetArchitecture.X64 => "X64",
            TargetArchitecture.ARM => "ARM",
            TargetArchitecture.ARM64 => "ARM64",
            _ => null,
        };
        if (machine is not null)
        {
            context.Add("/MACHINE:" + machine);
        }

        CppOutputKind kind = context.Get(OutputKind);
        context.Reject(OutputKind, kind == CppOutputKind.AppleBundle);
        if (kind == CppOutputKind.SharedLibrary)
        {
            context.Add("/DLL");
        }

        if (context.Get(Subsystem) is string subsystem)
        {
            context.Add("/SUBSYSTEM:" + subsystem);
        }

        if (context.Get(ImportLibrary) is string importLibrary)
        {
            context.Add("/IMPLIB:" + importLibrary);
        }

        if (context.Get(EntryPoint) is string entry)
        {
            context.Add("/ENTRY:" + entry);
        }

        if (context.Get(Pdb) is string pdb)
        {
            context.Add("/PDB:" + pdb);
        }

        if (context.Get(Debug) != CppDebugFormat.None)
        {
            context.Add("/DEBUG:FULL");
        }

        if (context.Get(Lto) != CppLto.None)
        {
            if (context.Get(LinkerDialect) == CppLinkerDialect.Msvc)
            {
                context.Reject(Lto, context.Dialect != CppDialect.Msvc,
                    "Clang LTO objects require lld-link, not the MSVC linker.");
                context.Add("/LTCG", "/INCREMENTAL:NO");
            }
            else
            {
                context.Reject(Lto, context.Dialect == CppDialect.Msvc,
                    "MSVC /GL objects require the matching MSVC linker.");
            }
        }

        if (context.Get(DynamicDebug))
        {
            context.Reject(DynamicDebug, context.Dialect != CppDialect.Msvc || context.Get(LinkerDialect) != CppLinkerDialect.Msvc);
            CppFeatureValidation.DynamicDebug(context);
            context.Add("/DEBUG:FULL", "/dynamicdeopt", "/INCREMENTAL:NO", "/OPT:NOICF");
        }

        if (context.List(ManifestInputs).Count > 0)
        {
            context.Add("/MANIFEST:EMBED");
            foreach (string manifest in context.List(ManifestInputs))
            {
                context.Add("/MANIFESTINPUT:" + manifest);
            }
        }

        if (context.Has(WindowsRuntime))
        {
            string selected = context.Get(WindowsRuntime) switch
            {
                CppWindowsRuntime.MD => "msvcrt",
                CppWindowsRuntime.MDd => "msvcrtd",
                CppWindowsRuntime.MT => "libcmt",
                _ => "libcmtd",
            };
            foreach (string runtime in new[] { "msvcrt", "msvcrtd", "libcmt", "libcmtd" })
            {
                if (runtime != selected)
                {
                    context.Add("/NODEFAULTLIB:" + runtime);
                }
            }

            context.Add("/DEFAULTLIB:" + selected);
        }

        if (context.Get(DisableDefaultLibraries))
        {
            context.Add("/NODEFAULTLIB");
        }

        foreach (string library in context.List(NoDefaultLibraries))
        {
            context.Add("/NODEFAULTLIB:" + library);
        }

        foreach (string export in context.List(Exports))
        {
            context.Add("/EXPORT:" + export);
        }

        context.Reject(Rpaths, context.List(Rpaths).Count > 0);
        context.Reject(Frameworks, context.List(Frameworks).Count > 0);
        context.Reject(Soname, context.Has(Soname));
        context.Reject(InstallName, context.Has(InstallName));
        context.Reject(BundleLoader, context.Has(BundleLoader));
    }

    private static void Driver(CppGenerationContext context)
    {
        if (context.Get(LinkerSelection) is string linker)
        {
            context.Reject(LinkerSelection, context.Dialect == CppDialect.Gnu && linker is not ("bfd" or "gold" or "lld" or "mold"),
                "GCC linker selection requires a named implementation.");
            context.Add("-fuse-ld=" + linker);
        }

        if (context.Get(LtoObjectPath) is string ltoObject)
        {
            context.Reject(LtoObjectPath, !context.Apple || context.Get(Lto) == CppLto.None);
            context.Linker("-object_path_lto", ltoObject);
        }
        else if (context.Apple && context.Get(Lto) != CppLto.None && context.Get(Debug) != CppDebugFormat.None)
        {
            context.Error(LtoObjectPath, "Apple LTO debug information requires a retained object path for later symbol processing.");
        }

        CppCompilerRuntime compilerRuntime = context.Get(CompilerRuntime);
        if (compilerRuntime != CppCompilerRuntime.Default)
        {
            context.Reject(CompilerRuntime, context.Dialect == CppDialect.Gnu && compilerRuntime == CppCompilerRuntime.CompilerRt);
            context.Reject(CompilerRuntime, context.Wasm && compilerRuntime == CppCompilerRuntime.LibGcc);
            if (context.Dialect != CppDialect.Gnu)
            {
                context.Add("-rtlib=" + (compilerRuntime == CppCompilerRuntime.CompilerRt ? "compiler-rt" : "libgcc"));
            }
        }

        CppOutputKind kind = context.Get(OutputKind);
        context.Reject(OutputKind, context.Dialect == CppDialect.WasiClang && kind != CppOutputKind.Executable,
            "WASI shared-library linking is not a stable supported operation.");
        if (kind == CppOutputKind.SharedLibrary && context.Dialect != CppDialect.Emscripten)
        {
            context.Add(context.Apple ? "-dynamiclib" : "-shared");
        }
        else if (kind == CppOutputKind.AppleBundle)
        {
            context.Reject(OutputKind, !context.Apple);
            context.Add("-bundle");
        }

        if (context.Get(PositionIndependentExecutable))
        {
            context.Add("-pie");
        }

        if (context.Get(Debug) != CppDebugFormat.None)
        {
            context.Add("-g");
        }

        if (context.Get(Soname) is string soname)
        {
            context.Reject(Soname, context.Apple || context.Wasm || context.Get(Platform) == TargetPlatform.Windows);
            context.Linker("-soname", soname);
        }

        if (context.Get(InstallName) is string installName)
        {
            context.Reject(InstallName, !context.Apple);
            context.Linker("-install_name", installName);
        }

        if (context.Get(BundleLoader) is string loader)
        {
            context.Reject(BundleLoader, !context.Apple || kind != CppOutputKind.AppleBundle);
            context.Add("-bundle_loader", loader);
        }

        if (context.Get(EntryPoint) is string entry)
        {
            context.Linker("-e", entry);
        }

        foreach (string directory in context.List(FrameworkDirectories))
        {
            context.Reject(FrameworkDirectories, !context.Apple);
            context.Add("-F", directory);
        }

        foreach (string framework in context.List(Frameworks))
        {
            context.Reject(Frameworks, !context.Apple);
            context.Add("-framework", framework);
        }

        foreach (string path in context.List(Rpaths))
        {
            context.Reject(Rpaths, context.Wasm || context.Get(Platform) == TargetPlatform.Windows);
            context.Linker("-rpath", path);
        }

        if (context.Has(UseRunpath))
        {
            context.Reject(UseRunpath, context.Apple || context.Wasm || context.Get(Platform) == TargetPlatform.Windows);
            context.Linker(context.Get(UseRunpath) ? "--enable-new-dtags" : "--disable-new-dtags");
        }

        if (context.Get(DisableDefaultLibraries))
        {
            context.Add("-nodefaultlibs");
        }

        context.Reject(NoDefaultLibraries, context.List(NoDefaultLibraries).Count > 0,
            "This linker cannot suppress a single implicit default library by name.");
        foreach (string export in context.List(Exports))
        {
            if (context.Apple)
            {
                context.Linker("-exported_symbol", export);
            }
            else if (context.Wasm)
            {
                context.Linker("--export=" + export);
            }
            else
            {
                context.Reject(Exports, context.Get(Platform) == TargetPlatform.Windows,
                    "GNU Windows exports require a module-definition file supplied as a link input.");
                context.Linker("--export-dynamic-symbol=" + export);
            }
        }

        if (context.Get(ImportLibrary) is string importLibrary)
        {
            context.Reject(ImportLibrary, context.Get(Platform) != TargetPlatform.Windows);
            context.Linker("--out-implib", importLibrary);
        }

        context.Reject(ManifestInputs, context.Has(ManifestInputs));
        context.Reject(Pdb, context.Has(Pdb));
        context.Reject(DynamicDebug, context.Get(DynamicDebug));
        if (context.Get(WasiEntry) != CppWasiEntry.Default)
        {
            context.Reject(WasiEntry, context.Dialect != CppDialect.WasiClang);
            context.Add(context.Get(WasiEntry) == CppWasiEntry.Reactor ? "-mexec-model=reactor" : "-mexec-model=command");
        }
    }

    private static void Input(CppGenerationContext context, CppLinkInput input, bool microsoft, bool wholeArchive = false)
    {
        switch (input.Kind)
        {
            case CppLinkInputKind.File:
                context.Add(input.Value!);
                break;
            case CppLinkInputKind.Library:
                context.Add(microsoft ? input.Value + ".lib" : "-l" + input.Value);
                break;
            case CppLinkInputKind.ExactLibrary:
                context.Reject(LinkInputs, context.Apple, "Apple linkers require a resolved file path for exact library inputs.");
                context.Add(microsoft ? input.Value! : "-l:" + input.Value);
                break;
            case CppLinkInputKind.Group:
                context.Reject(LinkInputs, microsoft || context.Apple,
                    "Repeated archive search groups are a GNU-style linker feature.");
                context.Linker("--start-group");
                foreach (CppLinkInput child in input.Children)
                {
                    Input(context, child, microsoft, wholeArchive);
                }

                context.Linker("--end-group");
                break;
            case CppLinkInputKind.WholeArchive:
                if (microsoft || context.Apple)
                {
                    foreach (CppLinkInput child in input.Children)
                    {
                        context.Reject(LinkInputs, child.Kind is not (CppLinkInputKind.File or CppLinkInputKind.Library),
                            "Whole-archive operands must be individual archives on this linker.");
                        if (microsoft)
                        {
                            context.Add("/WHOLEARCHIVE:" + child.Value + (child.Kind == CppLinkInputKind.Library ? ".lib" : ""));
                        }
                        else
                        {
                            context.Reject(LinkInputs, child.Kind != CppLinkInputKind.File,
                                "Apple -force_load requires a resolved archive path.");
                            context.Linker("-force_load", child.Value!);
                        }
                    }
                }
                else
                {
                    if (!wholeArchive)
                    {
                        context.Linker("--whole-archive");
                    }

                    foreach (CppLinkInput child in input.Children)
                    {
                        Input(context, child, microsoft, wholeArchive: true);
                    }

                    if (!wholeArchive)
                    {
                        context.Linker("--no-whole-archive");
                    }
                }

                break;
        }
    }
}
