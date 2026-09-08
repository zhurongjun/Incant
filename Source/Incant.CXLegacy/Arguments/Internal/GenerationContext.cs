using LinkerSyntax = Incant.CXLegacy.Arguments.LinkerDialect;
using SourceLanguage = Incant.CXLegacy.Arguments.Language;

namespace Incant.CXLegacy.Arguments;

internal sealed partial class GenerationContext(ArgumentSet values, Dialect dialect, Operation operation)
{
    private readonly HashSet<ArgumentField> _consumed = [];
    private readonly List<string> _arguments = [];
    private readonly List<ArgumentDiagnostic> _diagnostics = [];

    internal ArgumentSet Values { get; } = values;

    internal Dialect Dialect { get; } = dialect;

    internal Operation Operation { get; } = operation;

    internal bool Microsoft => Dialect is Dialect.Msvc or Dialect.ClangCl;

    internal bool Clang => Dialect != Dialect.Msvc && Dialect != Dialect.Gnu;

    internal bool Wasm => Dialect is Dialect.Emscripten or Dialect.WasiClang;

    internal bool Apple => Platform is TargetPlatform.MacOS or TargetPlatform.IOS or TargetPlatform.IOSSimulator
        or TargetPlatform.TvOS or TargetPlatform.TvOSSimulator or TargetPlatform.WatchOS or TargetPlatform.WatchOSSimulator
        or TargetPlatform.VisionOS or TargetPlatform.VisionOSSimulator;

    internal bool Cpp => (Language ?? SourceLanguage.Cpp) is SourceLanguage.Cpp or SourceLanguage.ObjectiveCpp;

    private bool UsesDirectLink => (LinkerDialect ?? LinkerSyntax.Driver) != LinkerSyntax.Driver;

    internal bool Has(ArgumentField field)
    {
        _consumed.Add(field);
        return Values.IsSet(field);
    }

    internal void Add(params string[] arguments)
    {
        foreach (string argument in arguments)
        {
            if (argument is null || argument.Contains('\0'))
            {
                Error(ArgumentField.Raw, "A generated argument contains null or NUL.");
            }
            else
            {
                _arguments.Add(argument);
            }
        }
    }

    internal void Error(ArgumentField field, string reason) => _diagnostics.Add(new ArgumentDiagnostic(
        ArgumentDiagnosticSeverity.Error, field, reason, Values.Origins(field)));

    internal void Reject(ArgumentField field, bool present, string? reason = null)
    {
        if (present)
        {
            Error(field, reason ?? $"This setting is unsupported by {Dialect} during {Operation}.");
        }
    }

    internal void MinimumVersion(ArgumentField field, Version minimum)
    {
        Version? actual = CompilerVersion;
        if (actual is null || actual < minimum)
        {
            Error(field, $"This feature requires a confirmed {Dialect} version >= {minimum}.");
        }
    }

    internal string Required(ArgumentField field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Error(field, "A non-empty value is required for this operation.");
            return "";
        }

        return value;
    }

    internal void Linker(params string[] tokens)
    {
        foreach (string token in tokens)
        {
            if (UsesDirectLink)
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
        foreach (ArgumentField field in Values.Fields.Where(field => ArgumentUsage.Applies(field, Operation, UsesDirectLink)))
        {
            object value = Values.ReadValue(field)!;
            if (value is string text && text.Contains('\0'))
            {
                Error(field, "Strings cannot contain NUL.");
            }
            else if (value is Pch pch && !Enum.IsDefined(pch.Mode)
                || value is Dependencies dependencies && !Enum.IsDefined(dependencies.Mode))
            {
                Error(field, "The operation mode is not defined.");
            }
            else if (value is Enum enumeration && !Enum.IsDefined(enumeration.GetType(), enumeration))
            {
                Error(field, "The enum value is not defined.");
            }
            else if (value is IEnumerable<string> strings && strings.Any(item => item is null || item.Contains('\0')))
            {
                Error(field, "String collections cannot contain null or NUL.");
            }
            else if (value is IEnumerable<RawArgument> raw && raw.Any(item => item is null
                || !Enum.IsDefined(item.Route) || !Enum.IsDefined(item.Position)
                || item.Operation is Operation selectedOperation && !Enum.IsDefined(selectedOperation)
                || item.Dialect is Dialect selectedDialect && !Enum.IsDefined(selectedDialect)
                || item.Language is Language selectedLanguage && !Enum.IsDefined(selectedLanguage)))
            {
                Error(field, "Raw argument selectors and positions must be defined.");
            }
            else if (value is IEnumerable<LinkInput> links && links.Any(item => item is null))
            {
                Error(field, "Link inputs cannot contain null.");
            }
            else if (value is IEnumerable<Sanitizer> sanitizers && sanitizers.Any(item => !Enum.IsDefined(item)))
            {
                Error(field, "The sanitizer value is not defined.");
            }
        }

        return _diagnostics.Count == 0;
    }

    internal ArgumentGenerationResult Failure() => new([], _diagnostics);

    internal ArgumentGenerationResult Finish()
    {
        // Context is declarative even when the invocation itself selects a fixed target.
        _consumed.UnionWith([ArgumentField.Platform, ArgumentField.Architecture, ArgumentField.CompilerVersion]);
        foreach (ArgumentField field in Values.Fields)
        {
            if (!_consumed.Contains(field) && ArgumentUsage.Applies(field, Operation, UsesDirectLink))
            {
                Error(field, $"This setting is relevant to {Operation}, but unsupported in the selected configuration.");
            }
        }

        return new ArgumentGenerationResult(_arguments, _diagnostics);
    }
}

internal static class ArgumentUsage
{
    private static readonly IReadOnlySet<ArgumentField> s_compile = new HashSet<ArgumentField>
    {
        ArgumentField.Language, ArgumentField.Standard, ArgumentField.Inputs, ArgumentField.Output, ArgumentField.Platform,
        ArgumentField.Architecture, ArgumentField.CompilerVersion, ArgumentField.Triple, ArgumentField.Sysroot,
        ArgumentField.Multilib, ArgumentField.AndroidApi, ArgumentField.DeploymentVersion, ArgumentField.Optimization,
        ArgumentField.FloatingPoint, ArgumentField.Exceptions, ArgumentField.Rtti,
        ArgumentField.PositionIndependent, ArgumentField.PositionIndependentExecutable, ArgumentField.Threads,
        ArgumentField.FunctionSections, ArgumentField.DataSections,
        ArgumentField.Visibility, ArgumentField.Cpu, ArgumentField.InstructionSet, ArgumentField.Warnings,
        ArgumentField.WarningsAsErrors, ArgumentField.Debug, ArgumentField.Pdb, ArgumentField.DynamicDebug,
        ArgumentField.Lto,
        ArgumentField.StandardLibrary, ArgumentField.WindowsRuntime, ArgumentField.Sanitizers, ArgumentField.Includes,
        ArgumentField.SystemIncludes, ArgumentField.ForcedIncludes, ArgumentField.Undefines,
        ArgumentField.Defines, ArgumentField.WarningControls, ArgumentField.Pch, ArgumentField.Dependencies,
        ArgumentField.BigObject, ArgumentField.FullSourcePaths, ArgumentField.ConformingPreprocessor,
        ArgumentField.FrameworkDirectories, ArgumentField.WasmModule, ArgumentField.WasmLegacyExceptions, ArgumentField.Raw,
    };

    private static readonly IReadOnlySet<ArgumentField> s_link = new HashSet<ArgumentField>
    {
        ArgumentField.Language, ArgumentField.Inputs, ArgumentField.LinkInputs, ArgumentField.Output,
        ArgumentField.OutputKind, ArgumentField.Platform, ArgumentField.Architecture, ArgumentField.CompilerVersion,
        ArgumentField.Triple, ArgumentField.Sysroot, ArgumentField.Multilib, ArgumentField.AndroidApi,
        ArgumentField.DeploymentVersion, ArgumentField.Optimization, ArgumentField.FloatingPoint, ArgumentField.Exceptions,
        ArgumentField.PositionIndependentExecutable, ArgumentField.Threads, ArgumentField.Debug, ArgumentField.Pdb,
        ArgumentField.DynamicDebug, ArgumentField.Lto, ArgumentField.StandardLibrary,
        ArgumentField.RuntimeLinkage, ArgumentField.RuntimeLibraries, ArgumentField.CompilerRuntime,
        ArgumentField.LinkerSelection, ArgumentField.LtoObjectPath, ArgumentField.WindowsRuntime,
        ArgumentField.Sanitizers, ArgumentField.LibraryDirectories, ArgumentField.FrameworkDirectories, ArgumentField.Frameworks,
        ArgumentField.Rpaths, ArgumentField.Exports, ArgumentField.NoDefaultLibraries,
        ArgumentField.DisableDefaultLibraries, ArgumentField.Soname, ArgumentField.InstallName, ArgumentField.BundleLoader,
        ArgumentField.UseRunpath, ArgumentField.Subsystem, ArgumentField.EntryPoint, ArgumentField.ManifestInputs,
        ArgumentField.ImportLibrary, ArgumentField.LinkerDialect, ArgumentField.WasmModule, ArgumentField.WasiEntry,
        ArgumentField.WasmEnvironment, ArgumentField.WasmInitialMemory, ArgumentField.WasmMaximumMemory,
        ArgumentField.WasmStackSize, ArgumentField.WasmMemoryGrowth,
        ArgumentField.WasmModularize, ArgumentField.WasmExportName, ArgumentField.WasmLegacyExceptions,
        ArgumentField.WasmExports, ArgumentField.WasmRuntimeExports, ArgumentField.Raw,
    };

    private static readonly IReadOnlySet<ArgumentField> s_archive = new HashSet<ArgumentField>
    {
        ArgumentField.Inputs, ArgumentField.Output, ArgumentField.Lto, ArgumentField.DynamicDebug,
        ArgumentField.ArchiveMode, ArgumentField.ArchiveDialect, ArgumentField.ArchiveSupportsLto,
        ArgumentField.DeterministicArchive, ArgumentField.Raw,
    };

    private static readonly IReadOnlySet<ArgumentField> s_resource = new HashSet<ArgumentField>
    {
        ArgumentField.Inputs, ArgumentField.Output, ArgumentField.Platform, ArgumentField.Includes,
        ArgumentField.SystemIncludes, ArgumentField.ForcedIncludes, ArgumentField.Undefines, ArgumentField.Defines,
        ArgumentField.Raw,
    };

    private static readonly IReadOnlySet<ArgumentField> s_frontendLink = new HashSet<ArgumentField>
    {
        ArgumentField.Language, ArgumentField.Triple, ArgumentField.Sysroot, ArgumentField.Multilib,
        ArgumentField.AndroidApi, ArgumentField.DeploymentVersion, ArgumentField.Optimization, ArgumentField.FloatingPoint,
        ArgumentField.Exceptions, ArgumentField.Threads, ArgumentField.StandardLibrary,
    };

    internal static bool Applies(ArgumentField field, Operation operation, bool directLink)
    {
        if (directLink && operation == Operation.Link && s_frontendLink.Contains(field))
        {
            return false;
        }

        return operation switch
        {
            Operation.Compile => s_compile.Contains(field),
            Operation.Link => s_link.Contains(field),
            Operation.Archive => s_archive.Contains(field),
            Operation.Resource => s_resource.Contains(field),
            _ => false,
        };
    }
}

internal static class FeatureValidation
{
    internal static void DynamicDebug(GenerationContext context)
    {
        context.Reject(ArgumentField.DynamicDebug, context.Dialect != Dialect.Msvc);
        context.MinimumVersion(ArgumentField.DynamicDebug, new Version(19, 44));
        context.Reject(ArgumentField.DynamicDebug, (context.Architecture ?? TargetArchitecture.Unknown) != TargetArchitecture.X64,
            "Dynamic Debug requires a confirmed x64 compilation architecture.");
        context.Reject(ArgumentField.DynamicDebug, (context.Lto ?? Lto.None) != Lto.None
            || (context.Sanitizers ?? []).Contains(Sanitizer.Address), "Dynamic Debug is incompatible with LTO and AddressSanitizer.");
        context.Reject(ArgumentField.Debug, context.Has(ArgumentField.Debug) && (context.Debug ?? DebugFormat.None) == DebugFormat.None,
            "Dynamic Debug requires debug information.");
    }
}
