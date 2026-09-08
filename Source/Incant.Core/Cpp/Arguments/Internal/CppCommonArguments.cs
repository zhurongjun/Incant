using System.Globalization;
using Incant.Core.Arguments;
using static Incant.Core.Cpp.Arguments.CppArguments;

namespace Incant.Core.Cpp.Arguments;

internal static class CppCommonArguments
{
    internal static void Raw(CppGenerationContext context, CppRawPosition position)
    {
        foreach (CppRawArgument raw in context.List(CppArguments.Raw))
        {
            if (raw.Operation is not null && raw.Operation != context.Operation
                || raw.Dialect is not null && raw.Dialect != context.Dialect
                || raw.Language is not null && raw.Language != context.Get(Language, CppLanguage.Cpp)
                || raw.Position != position)
            {
                continue;
            }

            switch (raw.Route)
            {
                case CppArgumentRoute.Driver:
                    context.Add(raw.Value);
                    break;
                case CppArgumentRoute.Linker when context.Operation == CppOperation.Link:
                    context.Linker(raw.Value);
                    break;
                case CppArgumentRoute.Frontend when context.Operation == CppOperation.Compile && context.Clang:
                    if (context.Dialect == CppDialect.ClangCl)
                    {
                        context.Add("/clang:-Xclang", "/clang:" + raw.Value);
                    }
                    else
                    {
                        context.Add("-Xclang", raw.Value);
                    }

                    break;
                default:
                    context.Error(CppArguments.Raw, "Raw argument routing is not supported by this operation or dialect.");
                    break;
            }
        }
    }

    internal static void Target(CppGenerationContext context)
    {
        string? triple = context.Get(Triple);
        bool vision = context.Get(Platform) is TargetPlatform.VisionOS or TargetPlatform.VisionOSSimulator;
        string? deploymentVersion = context.Get(DeploymentVersion);
        if (vision && deploymentVersion is not null)
        {
            context.Reject(DeploymentVersion, !context.Clang || context.Microsoft,
                "visionOS deployment targeting requires an Apple-capable Clang frontend.");
            context.Reject(Architecture, context.Get(Architecture) != TargetArchitecture.ARM64,
                "The current visionOS argument model requires ARM64.");
            string selected = "arm64-apple-xros" + deploymentVersion
                + (context.Get(Platform) == TargetPlatform.VisionOSSimulator ? "-simulator" : "");
            context.Reject(Triple, triple is not null && triple != selected,
                "The explicit triple and deployment configuration disagree.");
            triple = selected;
        }

        if (triple is null && context.Apple && context.Has(Architecture))
        {
            string? architecture = context.Get(Architecture) switch
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
            if (context.Dialect is CppDialect.Msvc or CppDialect.Gnu or CppDialect.Emscripten)
            {
                context.Error(Triple, "This driver selects its target through the invocation; an explicit target override is unsupported.");
            }
            else
            {
                if (context.Dialect == CppDialect.AndroidClang)
                {
                    int api = context.Get(AndroidApi);
                    if (api <= 0)
                    {
                        context.Error(AndroidApi, "An Android target override requires a positive API level.");
                    }

                    context.Reject(Triple, triple.Length == 0 || char.IsDigit(triple[^1]),
                        "Supply the unversioned Android triple; AndroidApi supplies the API suffix.");
                    triple += api.ToString(CultureInfo.InvariantCulture);
                }

                if (context.Dialect == CppDialect.AppleClang)
                {
                    context.Add("-target", triple);
                }
                else
                {
                    context.Add("--target=" + triple);
                }
            }
        }

        if (context.Get(Sysroot) is string sysroot)
        {
            if (context.Microsoft)
            {
                context.Error(Sysroot, "Windows compiler resource roots are supplied as include and library directories.");
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

        string? multilib = context.Get(Multilib);
        if (multilib is not null && multilib != ".")
        {
            if (context.Dialect == CppDialect.WasiClang)
            {
                context.Reject(Multilib, multilib != "eh", "Only the default and EH WASI resource variants are supported.");
                context.Reject(Exceptions, multilib == "eh" && context.Get(Exceptions) != CppExceptionMode.Wasm,
                    "The EH resource variant requires Wasm exceptions.");
            }
            else if (context.Dialect == CppDialect.Emscripten)
            {
                context.Reject(Multilib, multilib != "pic", "Declare additional library variants through matching explicit settings.");
            }
            else if (!context.Microsoft && multilib is "32" or "64" or "x32")
            {
                context.Add("-m" + multilib);
            }
            else
            {
                context.Error(Multilib, "Unrecognized ABI variant.");
            }
        }

        if (!vision && deploymentVersion is string deployment)
        {
            string? flag = context.Get(Platform) switch
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
                context.Error(DeploymentVersion, "Encode the deployment version in the explicit target triple for this platform.");
            }
            else
            {
                context.Add(flag + deployment);
            }
        }
    }

    internal static void CodeGeneration(CppGenerationContext context)
    {
        if (context.Has(Optimization))
        {
            CppOptimization optimization = context.Get(Optimization);
            context.Add(context.Microsoft ? optimization switch
            {
                CppOptimization.None => "/Od",
                CppOptimization.Smallest => "/O1",
                CppOptimization.Fast or CppOptimization.Faster => "/Ox",
                _ => "/O2",
            } : optimization switch
            {
                CppOptimization.None => "-O0",
                CppOptimization.Fast => "-O1",
                CppOptimization.Faster => "-O2",
                CppOptimization.Fastest => "-O3",
                _ => context.Dialect == CppDialect.Gnu ? "-Os" : "-Oz",
            });
        }

        if (context.Has(FloatingPoint))
        {
            CppFloatingPoint model = context.Get(FloatingPoint);
            if (context.Microsoft)
            {
                context.Add("/fp:" + model.ToString().ToLowerInvariant());
            }
            else if (context.Dialect == CppDialect.Gnu)
            {
                context.Add(model == CppFloatingPoint.Fast ? "-ffast-math" : "-fno-fast-math");
                if (model == CppFloatingPoint.Strict)
                {
                    context.Add("-frounding-math", "-ftrapping-math", "-ffp-contract=off");
                }
            }
            else
            {
                context.Add("-ffp-model=" + model.ToString().ToLowerInvariant());
            }
        }

        CppLto lto = context.Get(Lto);
        if (lto != CppLto.None)
        {
            if (context.Dialect == CppDialect.Msvc)
            {
                context.Reject(Lto, lto == CppLto.Thin, "MSVC does not implement ThinLTO.");
                context.Add("/GL");
            }
            else if (context.Dialect == CppDialect.Gnu)
            {
                context.Reject(Lto, lto == CppLto.Thin, "GCC does not implement ThinLTO.");
                context.Add("-flto");
            }
            else
            {
                if (lto == CppLto.Thin)
                {
                    context.MinimumVersion(Lto, context.Dialect == CppDialect.AppleClang ? new Version(8, 0) : new Version(3, 9));
                }

                context.Add(lto == CppLto.Thin ? "-flto=thin" : "-flto");
            }
        }

        if (context.Has(Threads))
        {
            context.Reject(Threads, context.Dialect == CppDialect.WasiClang && context.Get(Threads)
                && !(context.Get(Triple)?.Contains("threads", StringComparison.Ordinal) ?? false),
                "WASI threads require an explicitly selected threads target and matching resources.");
            context.Reject(Threads, context.Microsoft && context.Get(Threads), "The Windows threading runtime is selected through the CRT.");
            if (!context.Microsoft && context.Get(Threads))
            {
                context.Add("-pthread");
            }
        }

        if (context.Has(Exceptions) && (context.Cpp || context.Operation == CppOperation.Link))
        {
            CppExceptionMode mode = context.Get(Exceptions);
            if (context.Microsoft)
            {
                context.Reject(Exceptions, mode is CppExceptionMode.Wasm or CppExceptionMode.EmscriptenJavaScript);
                context.Add(mode == CppExceptionMode.Disabled ? "/EHs-c-" : "/EHsc");
            }
            else
            {
                context.Reject(Exceptions, !context.Wasm && mode == CppExceptionMode.Wasm);
                context.Reject(Exceptions, mode == CppExceptionMode.EmscriptenJavaScript && context.Dialect != CppDialect.Emscripten);
                context.Add(mode switch
                {
                    CppExceptionMode.Disabled => "-fno-exceptions",
                    CppExceptionMode.Wasm => "-fwasm-exceptions",
                    _ => "-fexceptions",
                });
                if (mode == CppExceptionMode.Wasm && context.Has(WasmLegacyExceptions))
                {
                    context.Add("-mllvm", "-wasm-use-legacy-eh=" + (context.Get(WasmLegacyExceptions) ? "true" : "false"));
                }
            }
        }

        Sanitizers(context);
        Runtime(context);
        Wasm(context);
    }

    private static void Sanitizers(CppGenerationContext context)
    {
        IReadOnlyList<CppSanitizer> sanitizers = context.List(CppArguments.Sanitizers);
        if (sanitizers.Contains(CppSanitizer.Address) && sanitizers.Any(item => item is CppSanitizer.Thread or CppSanitizer.Memory)
            || sanitizers.Contains(CppSanitizer.Thread) && sanitizers.Contains(CppSanitizer.Memory))
        {
            context.Error(CppArguments.Sanitizers, "Address, thread and memory sanitizers are mutually exclusive.");
        }

        foreach (CppSanitizer sanitizer in sanitizers.Distinct())
        {
            bool unsupported = context.Microsoft && sanitizer != CppSanitizer.Address
                || context.Dialect == CppDialect.Gnu && sanitizer == CppSanitizer.Memory
                || context.Wasm && sanitizer is not (CppSanitizer.Address or CppSanitizer.Undefined or CppSanitizer.Leak);
            context.Reject(CppArguments.Sanitizers, unsupported, $"{sanitizer} is not supported by this compiler family.");
            if (context.Dialect == CppDialect.Msvc)
            {
                context.MinimumVersion(CppArguments.Sanitizers, new Version(19, 28));
            }

            string name = sanitizer == CppSanitizer.HardwareAddress ? "hwaddress" : sanitizer.ToString().ToLowerInvariant();
            context.Add((context.Microsoft ? "/fsanitize=" : "-fsanitize=") + name);
        }
    }

    private static void Runtime(CppGenerationContext context)
    {
        if (context.Has(WindowsRuntime))
        {
            context.Reject(WindowsRuntime, !context.Microsoft);
            if (context.Microsoft)
            {
                context.Add("/" + context.Get(WindowsRuntime));
            }
        }

        CppStandardLibrary library = context.Get(StandardLibrary);
        if (library != CppStandardLibrary.Default && context.Cpp)
        {
            bool bundled = context.Dialect is CppDialect.AndroidClang or CppDialect.Emscripten or CppDialect.WasiClang;
            context.Reject(StandardLibrary, bundled && library != CppStandardLibrary.LibCpp,
                "This SDK is built around libc++; another C++ library requires an independently configured toolchain.");
            if (context.Microsoft || context.Dialect == CppDialect.Gnu && library != CppStandardLibrary.LibStdCpp)
            {
                context.Error(StandardLibrary, "The selected C++ standard library is incompatible with this driver.");
            }
            else if (context.Dialect != CppDialect.Gnu)
            {
                context.Add("-stdlib=" + (library == CppStandardLibrary.LibCpp ? "libc++" : "libstdc++"));
            }
        }

        if (context.Operation != CppOperation.Link || !context.Cpp)
        {
            return;
        }

        CppRuntimeLinkage linkage = context.Get(RuntimeLinkage);
        if (linkage == CppRuntimeLinkage.Default)
        {
            return;
        }

        context.Reject(RuntimeLinkage, context.Microsoft, "Select the Windows CRT through WindowsRuntime.");
        context.Reject(RuntimeLinkage, context.Wasm && linkage == CppRuntimeLinkage.Shared,
            "A shared native C++ runtime cannot be selected for this WebAssembly driver.");
        if (linkage == CppRuntimeLinkage.Static)
        {
            if (context.Dialect is CppDialect.AndroidClang or CppDialect.Emscripten or CppDialect.WasiClang)
            {
                context.Add("-static-libstdc++");
            }
            else if (library == CppStandardLibrary.LibCpp)
            {
                context.Reject(RuntimeLibraries, context.List(RuntimeLibraries).Count == 0,
                    "Static libc++ requires explicit runtime archives, including its ABI support; the driver cannot infer their installation.");
                context.Add("-nostdlib++");
            }
            else
            {
                context.Reject(StandardLibrary, library == CppStandardLibrary.Default && context.Dialect != CppDialect.Gnu,
                    "Static runtime linkage requires an explicit C++ library selection.");
                context.Add("-static-libstdc++");
            }
        }
    }

    private static void Wasm(CppGenerationContext context)
    {
        CppWasmModule module = context.Get(WasmModule);
        if (module != CppWasmModule.None)
        {
            context.Reject(WasmModule, context.Dialect != CppDialect.Emscripten);
            context.Add(module switch
            {
                CppWasmModule.Main => "-sMAIN_MODULE=1",
                CppWasmModule.MainDeadCodeElimination => "-sMAIN_MODULE=2",
                CppWasmModule.Side => "-sSIDE_MODULE=1",
                _ => "-sSIDE_MODULE=2",
            });
        }

        if (context.Operation != CppOperation.Link)
        {
            return;
        }

        Setting(context, WasmEnvironment, "ENVIRONMENT");
        Setting(context, WasmInitialMemory, "INITIAL_MEMORY");
        Setting(context, WasmMaximumMemory, "MAXIMUM_MEMORY");
        Setting(context, WasmStackSize, "STACK_SIZE");
        Setting(context, WasmMemoryGrowth, "ALLOW_MEMORY_GROWTH");
        Setting(context, WasmModularize, "MODULARIZE");
        Setting(context, WasmExportName, "EXPORT_NAME");
        foreach ((ArgumentKey<IReadOnlyList<string>> key, string name) in new[]
        {
            (WasmExports, "EXPORTED_FUNCTIONS"), (WasmRuntimeExports, "EXPORTED_RUNTIME_METHODS"),
        })
        {
            if (context.Has(key))
            {
                context.Reject(key, context.Dialect != CppDialect.Emscripten);
                context.Add("-s" + name + "=" + System.Text.Json.JsonSerializer.Serialize(context.List(key)));
            }
        }
    }

    private static void Setting<T>(CppGenerationContext context, ArgumentKey<T> key, string name)
    {
        if (!context.Has(key))
        {
            return;
        }

        context.Reject(key, context.Dialect != CppDialect.Emscripten);
        T value = context.Get(key);
        string text = value is bool boolean ? (boolean ? "1" : "0") : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        context.Reject(key, value is long number && number < 0, "Memory and stack sizes cannot be negative.");
        context.Add("-s" + name + "=" + text);
    }
}
