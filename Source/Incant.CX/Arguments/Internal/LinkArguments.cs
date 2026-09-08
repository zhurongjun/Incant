namespace Incant.CX.Arguments;

internal static class LinkArguments
{
    internal static void Generate(GenerationContext context)
    {
        bool microsoft = (context.LinkerDialect ?? LinkerDialect.Driver) is LinkerDialect.Msvc or LinkerDialect.LldLink;
        if (microsoft)
        {
            Microsoft(context);
        }
        else
        {
            context.Reject(ArgumentField.LinkerDialect, context.Microsoft,
                "A Microsoft-style frontend requires an explicitly selected LINK or lld-link interpreter.");
            CommonArguments.Target(context);
            CommonArguments.CodeGeneration(context);
            Driver(context);
        }

        foreach (string directory in context.LibraryDirectories ?? [])
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

        CommonArguments.Raw(context, RawPosition.BeforeInputs);
        context.Add((context.Inputs ?? []).ToArray());
        foreach (LinkInput input in context.LinkInputs ?? [])
        {
            Input(context, input, microsoft);
        }

        foreach (LinkInput runtime in context.RuntimeLibraries ?? [])
        {
            Input(context, runtime, microsoft);
        }

        if ((context.Inputs ?? []).Count == 0 && (context.LinkInputs ?? []).Count == 0)
        {
            context.Error(ArgumentField.Inputs, "A link command requires inputs.");
        }

        string output = context.Required(ArgumentField.Output, context.Output);
        if (microsoft)
        {
            context.Add("/OUT:" + output);
        }
        else
        {
            context.Add("-o", output);
        }
    }

    private static void Microsoft(GenerationContext context)
    {
        context.Add("/NOLOGO");
        string? machine = (context.Architecture ?? TargetArchitecture.Unknown) switch
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

        OutputKind kind = context.OutputKind ?? OutputKind.Executable;
        context.Reject(ArgumentField.OutputKind, kind == OutputKind.AppleBundle);
        if (kind == OutputKind.SharedLibrary)
        {
            context.Add("/DLL");
        }

        if (context.Subsystem is string subsystem)
        {
            context.Add("/SUBSYSTEM:" + subsystem);
        }

        if (context.ImportLibrary is string importLibrary)
        {
            context.Add("/IMPLIB:" + importLibrary);
        }

        if (context.EntryPoint is string entry)
        {
            context.Add("/ENTRY:" + entry);
        }

        if (context.Pdb is string pdb)
        {
            context.Add("/PDB:" + pdb);
        }

        if ((context.Debug ?? DebugFormat.None) != DebugFormat.None)
        {
            context.Add("/DEBUG:FULL");
        }

        if ((context.Lto ?? Lto.None) != Lto.None)
        {
            if ((context.LinkerDialect ?? LinkerDialect.Driver) == LinkerDialect.Msvc)
            {
                context.Reject(ArgumentField.Lto, context.Dialect != Dialect.Msvc, "Clang LTO objects require lld-link, not the MSVC linker.");
                context.Add("/LTCG", "/INCREMENTAL:NO");
            }
            else
            {
                context.Reject(ArgumentField.Lto, context.Dialect == Dialect.Msvc, "MSVC /GL objects require the matching MSVC linker.");
            }
        }

        if (context.DynamicDebug ?? false)
        {
            context.Reject(ArgumentField.DynamicDebug, context.Dialect != Dialect.Msvc || (context.LinkerDialect ?? LinkerDialect.Driver) != LinkerDialect.Msvc);
            FeatureValidation.DynamicDebug(context);
            context.Add("/DEBUG:FULL", "/dynamicdeopt", "/INCREMENTAL:NO", "/OPT:NOICF");
        }

        if ((context.ManifestInputs ?? []).Count > 0)
        {
            context.Add("/MANIFEST:EMBED");
            foreach (string manifest in context.ManifestInputs ?? [])
            {
                context.Add("/MANIFESTINPUT:" + manifest);
            }
        }

        if (context.Has(ArgumentField.WindowsRuntime))
        {
            string selected = (context.WindowsRuntime ?? WindowsRuntime.MD) switch
            {
                WindowsRuntime.MD => "msvcrt",
                WindowsRuntime.MDd => "msvcrtd",
                WindowsRuntime.MT => "libcmt",
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

        if ((context.DisableDefaultLibraries ?? false))
        {
            context.Add("/NODEFAULTLIB");
        }

        foreach (string library in context.NoDefaultLibraries ?? [])
        {
            context.Add("/NODEFAULTLIB:" + library);
        }

        foreach (string export in context.Exports ?? [])
        {
            context.Add("/EXPORT:" + export);
        }

        context.Reject(ArgumentField.Rpaths, (context.Rpaths ?? []).Count > 0);
        context.Reject(ArgumentField.Frameworks, (context.Frameworks ?? []).Count > 0);
        context.Reject(ArgumentField.Soname, context.Has(ArgumentField.Soname));
        context.Reject(ArgumentField.InstallName, context.Has(ArgumentField.InstallName));
        context.Reject(ArgumentField.BundleLoader, context.Has(ArgumentField.BundleLoader));
    }

    private static void Driver(GenerationContext context)
    {
        if (context.LinkerSelection is string linker)
        {
            context.Reject(ArgumentField.LinkerSelection, context.Dialect == Dialect.Gnu && linker is not ("bfd" or "gold" or "lld" or "mold"),
                "GCC linker selection requires a named implementation.");
            context.Add("-fuse-ld=" + linker);
        }

        if (context.LtoObjectPath is string ltoObject)
        {
            context.Reject(ArgumentField.LtoObjectPath, !context.Apple || (context.Lto ?? Lto.None) == Lto.None);
            context.Linker("-object_path_lto", ltoObject);
        }
        else if (context.Apple && (context.Lto ?? Lto.None) != Lto.None && (context.Debug ?? DebugFormat.None) != DebugFormat.None)
        {
            context.Error(ArgumentField.LtoObjectPath, "Apple LTO debug information requires a retained object path for later symbol processing.");
        }

        CompilerRuntime compilerRuntime = context.CompilerRuntime ?? CompilerRuntime.Default;
        if (compilerRuntime != CompilerRuntime.Default)
        {
            context.Reject(ArgumentField.CompilerRuntime, context.Dialect == Dialect.Gnu && compilerRuntime == CompilerRuntime.CompilerRt);
            context.Reject(ArgumentField.CompilerRuntime, context.Wasm && compilerRuntime == CompilerRuntime.LibGcc);
            if (context.Dialect != Dialect.Gnu)
            {
                context.Add("-rtlib=" + (compilerRuntime == CompilerRuntime.CompilerRt ? "compiler-rt" : "libgcc"));
            }
        }

        OutputKind kind = context.OutputKind ?? OutputKind.Executable;
        context.Reject(ArgumentField.OutputKind, context.Dialect == Dialect.WasiClang && kind != OutputKind.Executable,
            "WASI shared-library linking is not a stable supported operation.");
        if (kind == OutputKind.SharedLibrary && context.Dialect != Dialect.Emscripten)
        {
            context.Add(context.Apple ? "-dynamiclib" : "-shared");
        }
        else if (kind == OutputKind.AppleBundle)
        {
            context.Reject(ArgumentField.OutputKind, !context.Apple);
            context.Add("-bundle");
        }

        if ((context.PositionIndependentExecutable ?? false))
        {
            context.Add("-pie");
        }

        if ((context.Debug ?? DebugFormat.None) != DebugFormat.None)
        {
            context.Add("-g");
        }

        if (context.Soname is string soname)
        {
            context.Reject(ArgumentField.Soname, context.Apple || context.Wasm || (context.Platform ?? TargetPlatform.Unknown) == TargetPlatform.Windows);
            context.Linker("-soname", soname);
        }

        if (context.InstallName is string installName)
        {
            context.Reject(ArgumentField.InstallName, !context.Apple);
            context.Linker("-install_name", installName);
        }

        if (context.BundleLoader is string loader)
        {
            context.Reject(ArgumentField.BundleLoader, !context.Apple || kind != OutputKind.AppleBundle);
            context.Add("-bundle_loader", loader);
        }

        if (context.EntryPoint is string entry)
        {
            context.Linker("-e", entry);
        }

        foreach (string directory in context.FrameworkDirectories ?? [])
        {
            context.Reject(ArgumentField.FrameworkDirectories, !context.Apple);
            context.Add("-F", directory);
        }

        foreach (string framework in context.Frameworks ?? [])
        {
            context.Reject(ArgumentField.Frameworks, !context.Apple);
            context.Add("-framework", framework);
        }

        foreach (string path in context.Rpaths ?? [])
        {
            context.Reject(ArgumentField.Rpaths, context.Wasm || (context.Platform ?? TargetPlatform.Unknown) == TargetPlatform.Windows);
            context.Linker("-rpath", path);
        }

        if (context.Has(ArgumentField.UseRunpath))
        {
            context.Reject(ArgumentField.UseRunpath, context.Apple || context.Wasm || (context.Platform ?? TargetPlatform.Unknown) == TargetPlatform.Windows);
            context.Linker((context.UseRunpath ?? false) ? "--enable-new-dtags" : "--disable-new-dtags");
        }

        if ((context.DisableDefaultLibraries ?? false))
        {
            context.Add("-nodefaultlibs");
        }

        context.Reject(ArgumentField.NoDefaultLibraries, (context.NoDefaultLibraries ?? []).Count > 0,
            "This linker cannot suppress a single implicit default library by name.");
        foreach (string export in context.Exports ?? [])
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
                context.Reject(ArgumentField.Exports, (context.Platform ?? TargetPlatform.Unknown) == TargetPlatform.Windows,
                    "GNU Windows exports require a module-definition file supplied as a link input.");
                context.Linker("--export-dynamic-symbol=" + export);
            }
        }

        if (context.ImportLibrary is string importLibrary)
        {
            context.Reject(ArgumentField.ImportLibrary, (context.Platform ?? TargetPlatform.Unknown) != TargetPlatform.Windows);
            context.Linker("--out-implib", importLibrary);
        }

        context.Reject(ArgumentField.ManifestInputs, context.Has(ArgumentField.ManifestInputs));
        context.Reject(ArgumentField.Pdb, context.Has(ArgumentField.Pdb));
        context.Reject(ArgumentField.DynamicDebug, (context.DynamicDebug ?? false));
        if ((context.WasiEntry ?? WasiEntry.Default) != WasiEntry.Default)
        {
            context.Reject(ArgumentField.WasiEntry, context.Dialect != Dialect.WasiClang);
            context.Add((context.WasiEntry ?? WasiEntry.Default) == WasiEntry.Reactor ? "-mexec-model=reactor" : "-mexec-model=command");
        }
    }

    private static void Input(GenerationContext context, LinkInput input, bool microsoft, bool wholeArchive = false)
    {
        switch (input.Kind)
        {
            case LinkInputKind.File:
                context.Add(input.Value!);
                break;
            case LinkInputKind.Library:
                context.Add(microsoft ? input.Value + ".lib" : "-l" + input.Value);
                break;
            case LinkInputKind.ExactLibrary:
                context.Reject(ArgumentField.LinkInputs, context.Apple, "Apple linkers require a resolved file path for exact library inputs.");
                context.Add(microsoft ? input.Value! : "-l:" + input.Value);
                break;
            case LinkInputKind.Group:
                context.Reject(ArgumentField.LinkInputs, microsoft || context.Apple,
                    "Repeated archive search groups are a GNU-style linker feature.");
                context.Linker("--start-group");
                foreach (LinkInput child in input.Children)
                {
                    Input(context, child, microsoft, wholeArchive);
                }

                context.Linker("--end-group");
                break;
            case LinkInputKind.WholeArchive:
                if (microsoft || context.Apple)
                {
                    foreach (LinkInput child in input.Children)
                    {
                        context.Reject(ArgumentField.LinkInputs, child.Kind is not (LinkInputKind.File or LinkInputKind.Library),
                            "Whole-archive operands must be individual archives on this linker.");
                        if (microsoft)
                        {
                            context.Add("/WHOLEARCHIVE:" + child.Value + (child.Kind == LinkInputKind.Library ? ".lib" : ""));
                        }
                        else
                        {
                            context.Reject(ArgumentField.LinkInputs, child.Kind != LinkInputKind.File,
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

                    foreach (LinkInput child in input.Children)
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
