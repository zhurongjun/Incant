using Incant.Core.Arguments;
using static Incant.Core.Cpp.Arguments.CppArguments;

namespace Incant.Core.Cpp.Arguments;

internal sealed class CppGenerationContext(ArgumentSet values, CppDialect dialect, CppOperation operation)
{
    private readonly HashSet<ArgumentKey> _consumed = [];
    private readonly List<string> _arguments = [];
    private readonly List<ArgumentDiagnostic> _diagnostics = [];

    internal ArgumentSet Values { get; } = values;

    internal CppDialect Dialect { get; } = dialect;

    internal CppOperation Operation { get; } = operation;

    internal bool Microsoft => Dialect is CppDialect.Msvc or CppDialect.ClangCl;

    internal bool Clang => Dialect != CppDialect.Msvc && Dialect != CppDialect.Gnu;

    internal bool Wasm => Dialect is CppDialect.Emscripten or CppDialect.WasiClang;

    internal bool Apple => Get(Platform) is TargetPlatform.MacOS or TargetPlatform.IOS or TargetPlatform.IOSSimulator
        or TargetPlatform.TvOS or TargetPlatform.TvOSSimulator or TargetPlatform.WatchOS or TargetPlatform.WatchOSSimulator
        or TargetPlatform.VisionOS or TargetPlatform.VisionOSSimulator;

    internal bool Cpp => Get(Language, CppLanguage.Cpp) is CppLanguage.Cpp or CppLanguage.ObjectiveCpp;

    internal T Get<T>(ArgumentKey<T> key) => Get(key, default(T)!);

    internal T Get<T>(ArgumentKey<T> key, T fallback)
    {
        _consumed.Add(key);
        if (!Values.TryGet(key, out T? value))
        {
            return fallback;
        }

        if (value is Enum enumeration && !Enum.IsDefined(typeof(T), enumeration))
        {
            Error(key, "The enum value is not defined.");
            return fallback;
        }

        return value;
    }

    internal bool Has<T>(ArgumentKey<T> key)
    {
        _consumed.Add(key);
        return Values.TryGet(key, out _);
    }

    internal IReadOnlyList<T> List<T>(ArgumentKey<IReadOnlyList<T>> key) => Get(key, Array.Empty<T>());

    internal void Add(params string[] arguments)
    {
        foreach (string argument in arguments)
        {
            if (argument is null || argument.Contains('\0'))
            {
                Error(Raw, "A generated argument contains null or NUL.");
            }
            else
            {
                _arguments.Add(argument);
            }
        }
    }

    internal void Error(ArgumentKey key, string reason) => _diagnostics.Add(new ArgumentDiagnostic(
        ArgumentDiagnosticSeverity.Error, key.Id, reason, Values.Origins(key)));

    internal void Reject(ArgumentKey key, bool present, string? reason = null)
    {
        if (present)
        {
            Error(key, reason ?? $"This setting is unsupported by {Dialect} during {Operation}.");
        }
    }

    internal void MinimumVersion(ArgumentKey key, Version minimum)
    {
        Version? actual = Get(CompilerVersion);
        if (actual is null || actual < minimum)
        {
            Error(key, $"This feature requires a confirmed {Dialect} version >= {minimum}.");
        }
    }

    internal string Required(ArgumentKey<string> key)
    {
        string? value = Get(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            Error(key, "A non-empty value is required for this operation.");
            return "";
        }

        return value;
    }

    internal void Linker(params string[] tokens)
    {
        bool direct = Get(LinkerDialect) is CppLinkerDialect.Msvc or CppLinkerDialect.LldLink;
        foreach (string token in tokens)
        {
            if (direct)
            {
                Add(token);
            }
            else
            {
                Add("-Xlinker", token);
            }
        }
    }

    internal bool ValidateValues()
    {
        foreach (ArgumentKey key in Values.Keys.Where(key => CppArgumentUsage.Applies(key, Operation, Get(LinkerDialect) != CppLinkerDialect.Driver)))
        {
            object? value = Values.ReadSnapshot(key);
            if (value is null)
            {
                Error(key, "Remove an optional C++ field instead of setting it to null.");
            }
            else if (value is string text && text.Contains('\0'))
            {
                Error(key, "Strings cannot contain NUL.");
            }
            else if (value is CppPch pch && !Enum.IsDefined(pch.Mode)
                || value is CppDependencies dependencies && !Enum.IsDefined(dependencies.Mode))
            {
                Error(key, "The operation mode is not defined.");
            }
            else if (value is Enum enumeration && !Enum.IsDefined(enumeration.GetType(), enumeration))
            {
                Error(key, "The enum value is not defined.");
            }
            else if (value is IEnumerable<string> strings && strings.Any(item => item is null || item.Contains('\0')))
            {
                Error(key, "String collections cannot contain null or NUL.");
            }
            else if (value is IEnumerable<CppRawArgument> raw && raw.Any(item => item is null
                || !Enum.IsDefined(item.Route) || !Enum.IsDefined(item.Position)
                || item.Operation is CppOperation selectedOperation && !Enum.IsDefined(selectedOperation)
                || item.Dialect is CppDialect selectedDialect && !Enum.IsDefined(selectedDialect)
                || item.Language is CppLanguage selectedLanguage && !Enum.IsDefined(selectedLanguage)))
            {
                Error(key, "Raw argument selectors and positions must be defined.");
            }
            else if (value is IEnumerable<CppLinkInput> links && links.Any(item => item is null))
            {
                Error(key, "Link inputs cannot contain null.");
            }
            else if (value is IEnumerable<CppSanitizer> sanitizers && sanitizers.Any(item => !Enum.IsDefined(item)))
            {
                Error(key, "The sanitizer value is not defined.");
            }
        }

        return _diagnostics.Count == 0;
    }

    internal ArgumentGenerationResult Failure() => new([], _diagnostics);

    internal ArgumentGenerationResult Finish()
    {
        // Context is declarative even when the invocation itself selects a fixed target.
        _consumed.UnionWith([Platform, Architecture, CompilerVersion]);
        foreach (ArgumentKey key in Values.Keys)
        {
            if (!_consumed.Contains(key) && CppArgumentUsage.Applies(key, Operation, Get(LinkerDialect) != CppLinkerDialect.Driver))
            {
                Error(key, $"This setting is relevant to {Operation}, but unsupported in the selected configuration.");
            }
        }

        return new ArgumentGenerationResult(_arguments, _diagnostics);
    }
}

internal static class CppArgumentUsage
{
    private static readonly IReadOnlySet<ArgumentKey> s_compile = new HashSet<ArgumentKey>
    {
        Language, Standard, Inputs, Output, Platform, Architecture, CompilerVersion, Triple, Sysroot,
        Multilib, AndroidApi, DeploymentVersion, Optimization, FloatingPoint, Exceptions, Rtti,
        PositionIndependent, PositionIndependentExecutable, Threads, FunctionSections, DataSections,
        Visibility, Cpu, InstructionSet, Warnings, WarningsAsErrors, Debug, Pdb, DynamicDebug, Lto,
        StandardLibrary, WindowsRuntime, Sanitizers, Includes, SystemIncludes, ForcedIncludes, Undefines,
        Defines, WarningControls, Pch, Dependencies, BigObject, FullSourcePaths, ConformingPreprocessor,
        FrameworkDirectories, WasmModule, WasmLegacyExceptions, Raw,
    };

    private static readonly IReadOnlySet<ArgumentKey> s_link = new HashSet<ArgumentKey>
    {
        Language, Inputs, LinkInputs, Output, OutputKind, Platform, Architecture, CompilerVersion,
        Triple, Sysroot, Multilib, AndroidApi, DeploymentVersion, Optimization, FloatingPoint, Exceptions,
        PositionIndependentExecutable, Threads, Debug, Pdb, DynamicDebug, Lto, StandardLibrary,
        RuntimeLinkage, RuntimeLibraries, CompilerRuntime, LinkerSelection, LtoObjectPath, WindowsRuntime,
        Sanitizers, LibraryDirectories, FrameworkDirectories, Frameworks,
        Rpaths, Exports, NoDefaultLibraries, DisableDefaultLibraries, Soname, InstallName, BundleLoader,
        UseRunpath, Subsystem, EntryPoint, ManifestInputs, ImportLibrary, LinkerDialect, WasmModule, WasiEntry,
        WasmEnvironment, WasmInitialMemory, WasmMaximumMemory, WasmStackSize, WasmMemoryGrowth,
        WasmModularize, WasmExportName, WasmLegacyExceptions, WasmExports, WasmRuntimeExports, Raw,
    };

    private static readonly IReadOnlySet<ArgumentKey> s_archive = new HashSet<ArgumentKey>
    {
        Inputs, Output, Lto, DynamicDebug, ArchiveMode, ArchiveDialect, ArchiveSupportsLto, DeterministicArchive, Raw,
    };

    private static readonly IReadOnlySet<ArgumentKey> s_resource = new HashSet<ArgumentKey>
    {
        Inputs, Output, Platform, Includes, SystemIncludes, ForcedIncludes, Undefines, Defines, Raw,
    };

    private static readonly IReadOnlySet<ArgumentKey> s_frontendLink = new HashSet<ArgumentKey>
    {
        Language, Triple, Sysroot, Multilib, AndroidApi, DeploymentVersion, Optimization, FloatingPoint,
        Exceptions, Threads, StandardLibrary,
    };

    internal static bool Applies(ArgumentKey key, CppOperation operation, bool directLink)
    {
        if (directLink && operation == CppOperation.Link && s_frontendLink.Contains(key))
        {
            return false;
        }

        return operation switch
        {
            CppOperation.Compile => s_compile.Contains(key),
            CppOperation.Link => s_link.Contains(key),
            CppOperation.Archive => s_archive.Contains(key),
            CppOperation.Resource => s_resource.Contains(key),
            _ => false,
        };
    }
}

internal static class CppFeatureValidation
{
    internal static void DynamicDebug(CppGenerationContext context)
    {
        context.Reject(CppArguments.DynamicDebug, context.Dialect != CppDialect.Msvc);
        context.MinimumVersion(CppArguments.DynamicDebug, new Version(19, 44));
        context.Reject(CppArguments.DynamicDebug, context.Get(Architecture) != TargetArchitecture.X64,
            "Dynamic Debug requires a confirmed x64 compilation architecture.");
        context.Reject(CppArguments.DynamicDebug, context.Get(Lto) != CppLto.None
            || context.List(Sanitizers).Contains(CppSanitizer.Address),
            "Dynamic Debug is incompatible with LTO and AddressSanitizer.");
        context.Reject(Debug, context.Has(Debug) && context.Get(Debug) == CppDebugFormat.None,
            "Dynamic Debug requires debug information.");
    }
}
