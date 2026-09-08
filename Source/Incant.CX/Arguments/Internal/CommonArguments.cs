using System.Globalization;

namespace Incant.CX.Arguments;

internal static class CommonArguments
{
    internal static void Raw(GenerationContext context, RawPosition position)
    {
        foreach (RawArgument raw in context.Raw ?? [])
        {
            if (raw.Operation is not null && raw.Operation != context.Operation
                || raw.Dialect is not null && raw.Dialect != context.Dialect
                || raw.Language is not null && raw.Language != (context.Language ?? Language.Cpp)
                || raw.Position != position)
            {
                continue;
            }

            switch (raw.Route)
            {
                case ArgumentRoute.Driver:
                    context.Add(raw.Value);
                    break;
                case ArgumentRoute.Linker when context.Operation == Operation.Link:
                    context.Linker(raw.Value);
                    break;
                case ArgumentRoute.Frontend when context.Operation == Operation.Compile && context.Clang:
                    if (context.Dialect == Dialect.ClangCl)
                    {
                        context.Add("/clang:-Xclang", "/clang:" + raw.Value);
                    }
                    else
                    {
                        context.Add("-Xclang", raw.Value);
                    }

                    break;
                default:
                    context.Error(ArgumentField.Raw, "Raw argument routing is not supported by this operation or dialect.");
                    break;
            }
        }
    }

    internal static void Target(GenerationContext context)
    {
        string? triple = context.Triple;
        bool vision = (context.Platform ?? TargetPlatform.Unknown) is TargetPlatform.VisionOS or TargetPlatform.VisionOSSimulator;
        string? deploymentVersion = context.DeploymentVersion;
        if (vision && deploymentVersion is not null)
        {
            context.Reject(ArgumentField.DeploymentVersion, !context.Clang || context.Microsoft,
                "visionOS deployment targeting requires an Apple-capable Clang frontend.");
            context.Reject(ArgumentField.Architecture, (context.Architecture ?? TargetArchitecture.Unknown) != TargetArchitecture.ARM64,
                "The current visionOS argument model requires ARM64.");
            string selected = "arm64-apple-xros" + deploymentVersion
                + ((context.Platform ?? TargetPlatform.Unknown) == TargetPlatform.VisionOSSimulator ? "-simulator" : "");
            context.Reject(ArgumentField.Triple, triple is not null && triple != selected,
                "The explicit triple and deployment configuration disagree.");
            triple = selected;
        }

        if (triple is null && context.Apple && context.Has(ArgumentField.Architecture))
        {
            string? architecture = (context.Architecture ?? TargetArchitecture.Unknown) switch
            {
                TargetArchitecture.X64 => "x86_64",
                TargetArchitecture.ARM64 => "arm64",
                TargetArchitecture.X86 => "i386",
                TargetArchitecture.ARM => "armv7",
                _ => null,
            };
            if (architecture is not null)
            {
                context.Add("-arch", architecture);
            }
        }

        if (triple is not null)
        {
            if (context.Dialect is Dialect.Msvc or Dialect.Gnu or Dialect.Emscripten)
            {
                context.Error(ArgumentField.Triple,
                    "This driver selects its target through the invocation; an explicit target override is unsupported.");
            }
            else
            {
                if (context.Dialect == Dialect.AndroidClang)
                {
                    int api = context.AndroidApi ?? 0;
                    if (api <= 0)
                    {
                        context.Error(ArgumentField.AndroidApi, "An Android target override requires a positive API level.");
                    }

                    context.Reject(ArgumentField.Triple, triple.Length == 0 || char.IsDigit(triple[^1]),
                        "Supply the unversioned Android triple; AndroidApi supplies the API suffix.");
                    triple += api.ToString(CultureInfo.InvariantCulture);
                }

                if (context.Dialect == Dialect.AppleClang)
                {
                    context.Add("-target", triple);
                }
                else
                {
                    context.Add("--target=" + triple);
                }
            }
        }

        if (context.Sysroot is string sysroot)
        {
            if (context.Microsoft)
            {
                context.Error(ArgumentField.Sysroot, "Windows compiler resource roots are supplied as include and library directories.");
            }
            else if (context.Apple)
            {
                context.Add("-isysroot", sysroot);
            }
            else
            {
                context.Add("--sysroot=" + sysroot);
            }
        }

        string? multilib = context.Multilib;
        if (multilib is not null && multilib != ".")
        {
            if (context.Dialect == Dialect.WasiClang)
            {
                context.Reject(ArgumentField.Multilib, multilib != "eh", "Only the default and EH WASI resource variants are supported.");
                context.Reject(ArgumentField.Exceptions, multilib == "eh" && (context.Exceptions ?? ExceptionMode.Disabled) != ExceptionMode.Wasm,
                    "The EH resource variant requires Wasm exceptions.");
            }
            else if (context.Dialect == Dialect.Emscripten)
            {
                context.Reject(ArgumentField.Multilib, multilib != "pic", "Declare additional library variants through matching explicit settings.");
            }
            else if (!context.Microsoft && multilib is "32" or "64" or "x32")
            {
                context.Add("-m" + multilib);
            }
            else
            {
                context.Error(ArgumentField.Multilib, "Unrecognized ABI variant.");
            }
        }

        if (!vision && deploymentVersion is string deployment)
        {
            string? flag = (context.Platform ?? TargetPlatform.Unknown) switch
            {
                TargetPlatform.MacOS => "-mmacosx-version-min=",
                TargetPlatform.IOS => "-miphoneos-version-min=",
                TargetPlatform.IOSSimulator => "-mios-simulator-version-min=",
                TargetPlatform.TvOS => "-mtvos-version-min=",
                TargetPlatform.TvOSSimulator => "-mtvos-simulator-version-min=",
                TargetPlatform.WatchOS => "-mwatchos-version-min=",
                TargetPlatform.WatchOSSimulator => "-mwatchos-simulator-version-min=",
                _ => null,
            };
            if (flag is null)
            {
                context.Error(ArgumentField.DeploymentVersion, "Encode the deployment version in the explicit target triple for this platform.");
            }
            else
            {
                context.Add(flag + deployment);
            }
        }
    }

    internal static void CodeGeneration(GenerationContext context)
    {
        if (context.Has(ArgumentField.Optimization))
        {
            Optimization optimization = context.Optimization ?? Optimization.None;
            context.Add(context.Microsoft ? optimization switch
            {
                Optimization.None => "/Od",
                Optimization.Smallest => "/O1",
                Optimization.Fast or Optimization.Faster => "/Ox",
                _ => "/O2",
            } : optimization switch
            {
                Optimization.None => "-O0",
                Optimization.Fast => "-O1",
                Optimization.Faster => "-O2",
                Optimization.Fastest => "-O3",
                _ => context.Dialect == Dialect.Gnu ? "-Os" : "-Oz",
            });
        }

        if (context.Has(ArgumentField.FloatingPoint))
        {
            FloatingPoint model = context.FloatingPoint ?? FloatingPoint.Precise;
            if (context.Microsoft)
            {
                context.Add("/fp:" + model.ToString().ToLowerInvariant());
            }
            else if (context.Dialect == Dialect.Gnu)
            {
                context.Add(model == FloatingPoint.Fast ? "-ffast-math" : "-fno-fast-math");
                if (model == FloatingPoint.Strict)
                {
                    context.Add("-frounding-math", "-ftrapping-math", "-ffp-contract=off");
                }
            }
            else
            {
                context.Add("-ffp-model=" + model.ToString().ToLowerInvariant());
            }
        }

        Lto lto = context.Lto ?? Lto.None;
        if (lto != Lto.None)
        {
            if (context.Dialect == Dialect.Msvc)
            {
                context.Reject(ArgumentField.Lto, lto == Lto.Thin, "MSVC does not implement ThinLTO.");
                context.Add("/GL");
            }
            else if (context.Dialect == Dialect.Gnu)
            {
                context.Reject(ArgumentField.Lto, lto == Lto.Thin, "GCC does not implement ThinLTO.");
                context.Add("-flto");
            }
            else
            {
                if (lto == Lto.Thin)
                {
                    context.MinimumVersion(ArgumentField.Lto, context.Dialect == Dialect.AppleClang ? new Version(8, 0) : new Version(3, 9));
                }

                context.Add(lto == Lto.Thin ? "-flto=thin" : "-flto");
            }
        }

        if (context.Has(ArgumentField.Threads))
        {
            context.Reject(ArgumentField.Threads, context.Dialect == Dialect.WasiClang && (context.Threads ?? false)
                && !(context.Triple?.Contains("threads", StringComparison.Ordinal) ?? false), "WASI threads require an explicitly selected threads target and matching resources.");
            context.Reject(ArgumentField.Threads, context.Microsoft && (context.Threads ?? false),
                "The Windows threading runtime is selected through the CRT.");
            if (!context.Microsoft && (context.Threads ?? false))
            {
                context.Add("-pthread");
            }
        }

        if (context.Has(ArgumentField.Exceptions) && (context.Cpp || context.Operation == Operation.Link))
        {
            ExceptionMode mode = context.Exceptions ?? ExceptionMode.Disabled;
            if (context.Microsoft)
            {
                context.Reject(ArgumentField.Exceptions, mode is ExceptionMode.Wasm or ExceptionMode.EmscriptenJavaScript);
                context.Add(mode == ExceptionMode.Disabled ? "/EHs-c-" : "/EHsc");
            }
            else
            {
                context.Reject(ArgumentField.Exceptions, !context.Wasm && mode == ExceptionMode.Wasm);
                context.Reject(ArgumentField.Exceptions, mode == ExceptionMode.EmscriptenJavaScript && context.Dialect != Dialect.Emscripten);
                context.Add(mode switch
                {
                    ExceptionMode.Disabled => "-fno-exceptions",
                    ExceptionMode.Wasm => "-fwasm-exceptions",
                    _ => "-fexceptions",
                });
                if (mode == ExceptionMode.Wasm && context.Has(ArgumentField.WasmLegacyExceptions))
                {
                    context.Add("-mllvm", "-wasm-use-legacy-eh=" + ((context.WasmLegacyExceptions ?? false) ? "true" : "false"));
                }
            }
        }

        Sanitizers(context);
        Runtime(context);
        Wasm(context);
    }

    private static void Sanitizers(GenerationContext context)
    {
        IReadOnlyList<Sanitizer> sanitizers = context.Sanitizers ?? [];
        if (sanitizers.Contains(Sanitizer.Address) && sanitizers.Any(item => item is Sanitizer.Thread or Sanitizer.Memory)
            || sanitizers.Contains(Sanitizer.Thread) && sanitizers.Contains(Sanitizer.Memory))
        {
            context.Error(ArgumentField.Sanitizers, "Address, thread and memory sanitizers are mutually exclusive.");
        }

        foreach (Sanitizer sanitizer in sanitizers.Distinct())
        {
            bool unsupported = context.Microsoft && sanitizer != Sanitizer.Address
                || context.Dialect == Dialect.Gnu && sanitizer == Sanitizer.Memory
                || context.Wasm && sanitizer is not (Sanitizer.Address or Sanitizer.Undefined or Sanitizer.Leak);
            context.Reject(ArgumentField.Sanitizers, unsupported, $"{sanitizer} is not supported by this compiler family.");
            if (context.Dialect == Dialect.Msvc)
            {
                context.MinimumVersion(ArgumentField.Sanitizers, new Version(19, 28));
            }

            string name = sanitizer == Sanitizer.HardwareAddress ? "hwaddress" : sanitizer.ToString().ToLowerInvariant();
            context.Add((context.Microsoft ? "/fsanitize=" : "-fsanitize=") + name);
        }
    }

    private static void Runtime(GenerationContext context)
    {
        if (context.Has(ArgumentField.WindowsRuntime))
        {
            context.Reject(ArgumentField.WindowsRuntime, !context.Microsoft);
            if (context.Microsoft)
            {
                context.Add("/" + (context.WindowsRuntime ?? WindowsRuntime.MD));
            }
        }

        StandardLibrary library = context.StandardLibrary ?? StandardLibrary.Default;
        if (library != StandardLibrary.Default && context.Cpp)
        {
            bool bundled = context.Dialect is Dialect.AndroidClang or Dialect.Emscripten or Dialect.WasiClang;
            context.Reject(ArgumentField.StandardLibrary, bundled && library != StandardLibrary.LibCpp,
                "This SDK is built around libc++; another C++ library requires an independently configured toolchain.");
            if (context.Microsoft || context.Dialect == Dialect.Gnu && library != StandardLibrary.LibStdCpp)
            {
                context.Error(ArgumentField.StandardLibrary, "The selected C++ standard library is incompatible with this driver.");
            }
            else if (context.Dialect != Dialect.Gnu)
            {
                context.Add("-stdlib=" + (library == StandardLibrary.LibCpp ? "libc++" : "libstdc++"));
            }
        }

        if (context.Operation != Operation.Link || !context.Cpp)
        {
            return;
        }

        RuntimeLinkage linkage = context.RuntimeLinkage ?? RuntimeLinkage.Default;
        if (linkage == RuntimeLinkage.Default)
        {
            return;
        }

        context.Reject(ArgumentField.RuntimeLinkage, context.Microsoft, "Select the Windows CRT through WindowsRuntime.");
        context.Reject(ArgumentField.RuntimeLinkage, context.Wasm && linkage == RuntimeLinkage.Shared,
            "A shared native C++ runtime cannot be selected for this WebAssembly driver.");
        if (linkage == RuntimeLinkage.Static)
        {
            if (context.Dialect is Dialect.AndroidClang or Dialect.Emscripten or Dialect.WasiClang)
            {
                context.Add("-static-libstdc++");
            }
            else if (library == StandardLibrary.LibCpp)
            {
                context.Reject(ArgumentField.RuntimeLibraries, (context.RuntimeLibraries ?? []).Count == 0,
                    "Static libc++ requires explicit runtime archives, including its ABI support; the driver cannot infer their installation.");
                context.Add("-nostdlib++");
            }
            else
            {
                context.Reject(ArgumentField.StandardLibrary, library == StandardLibrary.Default && context.Dialect != Dialect.Gnu,
                    "Static runtime linkage requires an explicit C++ library selection.");
                context.Add("-static-libstdc++");
            }
        }
    }

    private static void Wasm(GenerationContext context)
    {
        WasmModule module = context.WasmModule ?? WasmModule.None;
        if (module != WasmModule.None)
        {
            context.Reject(ArgumentField.WasmModule, context.Dialect != Dialect.Emscripten);
            context.Add(module switch
            {
                WasmModule.Main => "-sMAIN_MODULE=1",
                WasmModule.MainDeadCodeElimination => "-sMAIN_MODULE=2",
                WasmModule.Side => "-sSIDE_MODULE=1",
                _ => "-sSIDE_MODULE=2",
            });
        }

        if (context.Operation != Operation.Link)
        {
            return;
        }

        Setting(context, ArgumentField.WasmEnvironment, context.WasmEnvironment, "ENVIRONMENT");
        Setting(context, ArgumentField.WasmInitialMemory, context.WasmInitialMemory, "INITIAL_MEMORY");
        Setting(context, ArgumentField.WasmMaximumMemory, context.WasmMaximumMemory, "MAXIMUM_MEMORY");
        Setting(context, ArgumentField.WasmStackSize, context.WasmStackSize, "STACK_SIZE");
        Setting(context, ArgumentField.WasmMemoryGrowth, context.WasmMemoryGrowth, "ALLOW_MEMORY_GROWTH");
        Setting(context, ArgumentField.WasmModularize, context.WasmModularize, "MODULARIZE");
        Setting(context, ArgumentField.WasmExportName, context.WasmExportName, "EXPORT_NAME");
        foreach ((ArgumentField field, string name, IReadOnlyList<string>? values) in new[]
        {
            (ArgumentField.WasmExports, "EXPORTED_FUNCTIONS", context.WasmExports), (ArgumentField.WasmRuntimeExports, "EXPORTED_RUNTIME_METHODS", context.WasmRuntimeExports),
        })
        {
            if (context.Has(field))
            {
                context.Reject(field, context.Dialect != Dialect.Emscripten);
                context.Add("-s" + name + "=" + System.Text.Json.JsonSerializer.Serialize((values ?? [])));
            }
        }
    }

    private static void Setting<T>(GenerationContext context, ArgumentField field, T selected, string name)
    {
        if (!context.Has(field))
        {
            return;
        }

        context.Reject(field, context.Dialect != Dialect.Emscripten);
        string text = selected is bool boolean ? (boolean ? "1" : "0") : Convert.ToString(selected, CultureInfo.InvariantCulture) ?? "";
        context.Reject(field, selected is long number && number < 0, "Memory and stack sizes cannot be negative.");
        context.Add("-s" + name + "=" + text);
    }
}
