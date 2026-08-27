using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using SB.Core;
using Serilog;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SB.Capabilities.BuildScripts;

public static class BuildScriptRuntime
{
    private const string RuntimeSchema = "sb-build-scripts-runtime-v2";
    private const string CompileOptionsSchema = "roslyn-csharp-release-unsafe-nullable-portablepdb-implicit-usings-v1";
    private const string AssemblyName = "SB.BuildScripts";
    private const string DllName = AssemblyName + ".dll";
    private const string PdbName = AssemblyName + ".pdb";
    private const int MaxCompileAttempts = 3;

    private static readonly Lock _loadLock = new();
    private static string? _loadedHash;
    private static string? _loadedSourceHash;

    private static readonly string[] BuildScriptRoots =
    [
        "Source",
        "Tests",
        "Thirdparty",
        "Tools",
    ];

    public static void EnsureLoaded()
    {
        using var trace = BuildTrace.Scope("BuildScriptRuntime.EnsureLoaded");
        lock (_loadLock)
        {
            var projectRoot = BuildTrace.Measure<string>("BuildScriptRuntime.FindProjectRoot", FindProjectRoot);
            var inputs = BuildTrace.Measure<BuildScriptInputs>("BuildScriptRuntime.BuildScriptInputs.Create", () => BuildScriptInputs.Create(projectRoot));
            if (_loadedHash == inputs.Hash)
            {
                BuildTrace.Mark("BuildScriptRuntime.EnsureLoaded.already_loaded", inputs.Hash);
                return;
            }
            if (_loadedHash is not null)
            {
                if (_loadedSourceHash == inputs.SourceFingerprint.Hash)
                {
                    Log.Verbose("Build scripts already loaded from cache {LoadedHash}; reusing it for current cache hash {CurrentHash}.", _loadedHash, inputs.Hash);
                    BuildTrace.Mark("BuildScriptRuntime.EnsureLoaded.reuse_loaded_source", $"loaded={_loadedHash} current={inputs.Hash}");
                    return;
                }
                throw new TaskFatalError($"Build scripts were already loaded from cache '{_loadedHash}', cannot load '{inputs.Hash}' in the same process because build script sources changed.");
            }

            var cacheRoot = Path.Combine(projectRoot, "build", ".sb", "capabilities", "build-scripts");
            var cacheDir = BuildTrace.Measure<string>("BuildScriptRuntime.EnsureCache", () => EnsureCache(cacheRoot, inputs));
            var dllPath = Path.Combine(cacheDir, DllName);
            if (!File.Exists(dllPath))
                throw new TaskFatalError($"Build script cache is missing output assembly: {dllPath}");

            try
            {
                BuildTrace.Measure("BuildScriptRuntime.LoadCacheAssembly", () => AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath));
                _loadedHash = inputs.Hash;
                _loadedSourceHash = inputs.SourceFingerprint.Hash;
                Log.Information("Loaded {Count} build scripts from {Cache}", inputs.Sources.Length, cacheDir);
                BuildTrace.Mark("BuildScriptRuntime.EnsureLoaded.loaded", $"sources={inputs.Sources.Length} references={inputs.References.Length}");
            }
            catch (Exception ex)
            {
                throw new TaskFatalError($"Failed to load build script cache '{cacheDir}'.", ex.ToString());
            }
        }
    }

    private static string FindProjectRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Tools", "SB", "SB.csproj")) &&
                File.Exists(Path.Combine(current.FullName, "Tools", "SB", "run_build.cs")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new TaskFatalError("Failed to locate SB project root from current directory.");
    }

    private static string EnsureCache(string cacheRoot, BuildScriptInputs initialInputs)
    {
        BuildTrace.Measure("BuildScriptRuntime.EnsureCache.CreateCacheRoot", () => Directory.CreateDirectory(cacheRoot));
        var inputs = initialInputs;
        for (var attempt = 1; attempt <= MaxCompileAttempts; attempt++)
        {
            var cacheDir = Path.Combine(cacheRoot, inputs.Hash);
            if (BuildTrace.Measure("BuildScriptRuntime.EnsureCache.IsValidCache", () => IsValidCache(cacheDir, inputs.Hash)))
            {
                BuildTrace.Mark("BuildScriptRuntime.EnsureCache.hit", $"attempt={attempt} hash={inputs.Hash}");
                return cacheDir;
            }

            BuildTrace.Mark("BuildScriptRuntime.EnsureCache.miss", $"attempt={attempt} hash={inputs.Hash}");
            var tmpDir = BuildTrace.Measure<string>("BuildScriptRuntime.EnsureCache.CreateTempDirectory", () => CreateTempDirectory(cacheRoot, inputs.Hash));
            try
            {
                BuildTrace.Measure("BuildScriptRuntime.EnsureCache.CompileToTemp", () => CompileToTemp(inputs, tmpDir));

                var afterCompileInputs = BuildTrace.Measure<BuildScriptInputs>("BuildScriptRuntime.EnsureCache.BuildScriptInputs.CreateAfterCompile", () => BuildScriptInputs.Create(inputs.ProjectRoot));
                if (afterCompileInputs.Hash != inputs.Hash)
                {
                    if (BuildTrace.Enabled)
                    {
                        var beforeReferences = inputs.References.Select(reference => reference.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var afterReferences = afterCompileInputs.References.Select(reference => reference.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var addedReferences = afterReferences
                            .Except(beforeReferences, StringComparer.OrdinalIgnoreCase)
                            .Select(Path.GetFileName)
                            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        var removedReferences = beforeReferences
                            .Except(afterReferences, StringComparer.OrdinalIgnoreCase)
                            .Select(Path.GetFileName)
                            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        BuildTrace.Mark(
                            "BuildScriptRuntime.EnsureCache.inputs_changed",
                            $"attempt={attempt} sources={inputs.Sources.Length}->{afterCompileInputs.Sources.Length} references={inputs.References.Length}->{afterCompileInputs.References.Length} added=[{string.Join(",", addedReferences)}] removed=[{string.Join(",", removedReferences)}]");
                    }
                    Directory.Delete(tmpDir, true);
                    inputs = afterCompileInputs;
                    continue;
                }

                BuildTrace.Measure("BuildScriptRuntime.EnsureCache.WriteManifest", () => WriteManifest(tmpDir, inputs, "compiled"));
                return BuildTrace.Measure<string>("BuildScriptRuntime.EnsureCache.PublishCache", () => PublishCache(cacheRoot, inputs.Hash, tmpDir));
            }
            catch (TaskFatalError)
            {
                throw;
            }
            catch (Exception ex)
            {
                WriteFailure(tmpDir, inputs, ex);
                throw new TaskFatalError("Failed to compile build scripts.", ex.ToString());
            }
        }
        throw new TaskFatalError("Build scripts changed while compiling; retry limit exceeded.");
    }

    private static string CreateTempDirectory(string cacheRoot, string hash)
    {
        var tmpParent = Path.Combine(cacheRoot, ".tmp", hash);
        Directory.CreateDirectory(tmpParent);
        var tmpDir = Path.Combine(tmpParent, $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        return tmpDir;
    }

    private static void CompileToTemp(BuildScriptInputs inputs, string tmpDir)
    {
        var parseOptions = new CSharpParseOptions(
            LanguageVersion.Latest,
            DocumentationMode.Parse,
            SourceCodeKind.Regular);
        var syntaxTrees = BuildTrace.Measure<List<SyntaxTree>>("BuildScriptRuntime.CompileToTemp.ParseSources", () =>
        {
            var trees = inputs.Sources.Select(source =>
                CSharpSyntaxTree.ParseText(
                    source.Content,
                    parseOptions,
                    source.FullPath,
                    Encoding.UTF8)).ToList();
            trees.Insert(0, CreateImplicitUsingsTree(parseOptions));
            return trees;
        });

        var references = BuildTrace.Measure<ImmutableArray<PortableExecutableReference>>("BuildScriptRuntime.CompileToTemp.CreateReferences", () => inputs.References
            .Select(reference => MetadataReference.CreateFromFile(reference.Path))
            .ToImmutableArray());
        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: Microsoft.CodeAnalysis.OptimizationLevel.Release,
            allowUnsafe: true,
            deterministic: true,
            nullableContextOptions: NullableContextOptions.Enable);
        var compilation = BuildTrace.Measure<CSharpCompilation>("BuildScriptRuntime.CompileToTemp.CreateCompilation", () => CSharpCompilation.Create(AssemblyName, syntaxTrees, references, compilationOptions));

        var dllPath = Path.Combine(tmpDir, DllName);
        var pdbPath = Path.Combine(tmpDir, PdbName);
        using var dllStream = File.Create(dllPath);
        using var pdbStream = File.Create(pdbPath);
        var emitResult = BuildTrace.Measure<EmitResult>("BuildScriptRuntime.CompileToTemp.Emit", () => compilation.Emit(
                dllStream,
                pdbStream,
                options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb)));

        var diagnostics = BuildTrace.Measure<BuildScriptDiagnostic[]>("BuildScriptRuntime.CompileToTemp.CollectDiagnostics", () => emitResult.Diagnostics.Select(BuildScriptDiagnostic.FromDiagnostic).ToArray());
        BuildTrace.Measure("BuildScriptRuntime.CompileToTemp.WriteDiagnostics", () => WriteDiagnostics(tmpDir, inputs, diagnostics));
        if (!emitResult.Success)
        {
            WriteManifest(tmpDir, inputs, "failed");
            var errors = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error.ToString()).ToArray();
            var message = errors.Length == 0
                ? "Build script compilation failed."
                : "Build script compilation failed:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(FormatDiagnostic));
            throw new TaskFatalError(message);
        }
    }

    private static string FormatDiagnostic(BuildScriptDiagnostic diagnostic)
    {
        var location = diagnostic.File is null
            ? string.Empty
            : diagnostic.Line is null
                ? diagnostic.File
                : diagnostic.Column is null
                    ? $"{diagnostic.File}({diagnostic.Line})"
                    : $"{diagnostic.File}({diagnostic.Line},{diagnostic.Column})";
        var severity = diagnostic.Severity.Length == 0 ? "error" : diagnostic.Severity.ToLowerInvariant();
        var prefix = location.Length == 0 ? $"{severity} {diagnostic.Id}" : $"{location}: {severity} {diagnostic.Id}";
        return $"{prefix}: {diagnostic.Message}";
    }

    private static SyntaxTree CreateImplicitUsingsTree(CSharpParseOptions parseOptions)
    {
        const string implicitUsings = """
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Threading;
            global using System.Threading.Tasks;
            """;
        return CSharpSyntaxTree.ParseText(
            implicitUsings,
            parseOptions,
            "SB.BuildScripts.ImplicitUsings.g.cs",
            Encoding.UTF8);
    }

    private static string PublishCache(string cacheRoot, string hash, string tmpDir)
    {
        var cacheDir = Path.Combine(cacheRoot, hash);
        using var publishLock = AcquirePublishLock(cacheRoot, hash);
        if (IsValidCache(cacheDir, hash))
        {
            Directory.Delete(tmpDir, true);
            return cacheDir;
        }

        try
        {
            Directory.Move(tmpDir, cacheDir);
        }
        catch (IOException) when (IsValidCache(cacheDir, hash))
        {
            Directory.Delete(tmpDir, true);
        }
        return cacheDir;
    }

    private static bool IsValidCache(string cacheDir, string hash)
    {
        var dllPath = Path.Combine(cacheDir, DllName);
        var manifestPath = Path.Combine(cacheDir, "manifest.json");
        if (!File.Exists(dllPath) || !File.Exists(manifestPath))
            return false;

        try
        {
            var manifest = JsonSerializer.Deserialize<BuildScriptManifest>(File.ReadAllText(manifestPath), BuildScriptJson.Options);
            return manifest is not null &&
                   manifest.Status == "compiled" &&
                   manifest.Hash == hash &&
                   manifest.OutputAssembly == DllName;
        }
        catch
        {
            return false;
        }
    }

    private static FileStream AcquirePublishLock(string cacheRoot, string hash)
    {
        var lockDir = Path.Combine(cacheRoot, ".locks");
        Directory.CreateDirectory(lockDir);
        var lockPath = Path.Combine(lockDir, $"{hash}.lock");
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(50);
            }
            catch (IOException ex)
            {
                throw new TaskFatalError($"Timed out waiting for build script publish lock: {lockPath}", ex.ToString());
            }
        }
    }

    private static void WriteDiagnostics(string directory, BuildScriptInputs inputs, BuildScriptDiagnostic[] diagnostics)
    {
        WriteJson(Path.Combine(directory, "diagnostics.json"), new BuildScriptDiagnosticsFile
        {
            Hash = inputs.Hash,
            Diagnostics = diagnostics,
        });
    }

    private static void WriteManifest(string directory, BuildScriptInputs inputs, string status)
    {
        WriteJson(Path.Combine(directory, "manifest.json"), new BuildScriptManifest
        {
            RuntimeSchema = RuntimeSchema,
            CompileOptionsSchema = CompileOptionsSchema,
            TargetFramework = inputs.TargetFramework,
            Status = status,
            Hash = inputs.Hash,
            SourceHash = inputs.SourceFingerprint.Hash,
            ReferenceHash = inputs.ReferenceFingerprint.Hash,
            ProjectRoot = inputs.ProjectRoot,
            OutputAssembly = DllName,
            OutputPdb = PdbName,
            Sources = inputs.Sources.Select(source => source.ToManifest()).ToArray(),
            References = inputs.References.Select(reference => reference.ToManifest()).ToArray(),
            CreatedUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
        });
    }

    private static void WriteFailure(string directory, BuildScriptInputs inputs, Exception ex)
    {
        WriteJson(Path.Combine(directory, "failure.json"), new BuildScriptFailure
        {
            Hash = inputs.Hash,
            Exception = ex.ToString(),
            CreatedUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
        });
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, BuildScriptJson.Options));
    }

    private sealed record BuildScriptInputs(
        string ProjectRoot,
        string TargetFramework,
        ImmutableArray<BuildScriptSource> Sources,
        ImmutableArray<BuildScriptReference> References,
        BuildScriptFingerprint SourceFingerprint,
        BuildScriptFingerprint ReferenceFingerprint,
        string Hash)
    {
        public static BuildScriptInputs Create(string projectRoot)
        {
            var targetFramework = AppContext.TargetFrameworkName ?? "unknown";
            var sources = BuildTrace.Measure<ImmutableArray<BuildScriptSource>>("BuildScriptInputs.DiscoverSources", () => DiscoverSources(projectRoot));
            BuildTrace.Mark("BuildScriptInputs.DiscoverSources.count", sources.Length.ToString());
            var references = BuildTrace.Measure<ImmutableArray<BuildScriptReference>>("BuildScriptInputs.DiscoverReferences", DiscoverReferences);
            BuildTrace.Mark("BuildScriptInputs.DiscoverReferences.count", references.Length.ToString());
            var sourceFingerprint = BuildTrace.Measure<BuildScriptFingerprint>("BuildScriptInputs.ComputeSourceFingerprint", () => ComputeSourceFingerprint(sources));
            var referenceFingerprint = BuildTrace.Measure<BuildScriptFingerprint>("BuildScriptInputs.ComputeReferenceFingerprint", () => ComputeReferenceFingerprint(references));
            var hash = BuildTrace.Measure<string>("BuildScriptInputs.HashParts", () => HashParts(
                    RuntimeSchema,
                    CompileOptionsSchema,
                    targetFramework,
                    sourceFingerprint.Hash,
                    referenceFingerprint.Hash));
            return new BuildScriptInputs(projectRoot, targetFramework, sources, references, sourceFingerprint, referenceFingerprint, hash);
        }

        private static BuildScriptFingerprint ComputeSourceFingerprint(ImmutableArray<BuildScriptSource> sources)
        {
            var builder = new StringBuilder();
            builder.AppendLine(RuntimeSchema);
            foreach (var source in sources)
            {
                builder.Append(source.RelativePath).Append('\0');
                builder.Append(source.ContentHash).AppendLine();
            }
            return new BuildScriptFingerprint(HashText(builder.ToString()));
        }

        private static BuildScriptFingerprint ComputeReferenceFingerprint(ImmutableArray<BuildScriptReference> references)
        {
            var builder = new StringBuilder();
            builder.AppendLine(RuntimeSchema);
            foreach (var reference in references)
            {
                builder.Append(reference.AssemblyName).Append('\0');
                builder.Append(reference.Path).Append('\0');
                builder.Append(reference.ModuleVersionId).Append('\0');
                builder.Append(reference.FileHash).AppendLine();
            }
            return new BuildScriptFingerprint(HashText(builder.ToString()));
        }

        private static ImmutableArray<BuildScriptSource> DiscoverSources(string projectRoot)
        {
            var paths = BuildScriptDiscovery.DiscoverBuildScriptPaths(projectRoot, BuildScriptRoots);
            var sources = paths
                .Select(path => BuildScriptSource.Create(projectRoot, path))
                .ToList();
            var localConfig = Path.Combine(projectRoot, "local_config.cs");
            if (File.Exists(localConfig))
                sources.Add(BuildScriptSource.Create(projectRoot, localConfig));

            return sources
                .OrderBy(source => source.RelativePath, StringComparer.Ordinal)
                .ToImmutableArray();
        }

        private static ImmutableArray<BuildScriptReference> DiscoverReferences()
        {
            var references = new Dictionary<string, BuildScriptReference>(StringComparer.OrdinalIgnoreCase);
            BuildTrace.Measure("BuildScriptInputs.DiscoverReferences.loaded_assemblies", () =>
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!string.IsNullOrEmpty(assembly.Location) && !IsBuildScriptAssembly(assembly.GetName().Name, assembly.Location))
                        AddReference(references, assembly.Location, assembly);
                }
            });

            var platformAssemblyPaths = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")?.ToString()?.Split(Path.PathSeparator) ?? [];
            BuildTrace.Measure("BuildScriptInputs.DiscoverReferences.trusted_platform_assemblies", () =>
            {
                foreach (var path in platformAssemblyPaths)
                {
                    if (!IsBuildScriptAssembly(System.IO.Path.GetFileNameWithoutExtension(path), path))
                        AddReference(references, path, null);
                }
            });

            BuildTrace.Measure("BuildScriptInputs.DiscoverReferences.app_base_dlls", () =>
            {
                foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    if (!IsBuildScriptAssembly(System.IO.Path.GetFileNameWithoutExtension(path), path))
                        AddReference(references, path, null);
                }
            });

            return BuildTrace.Measure<ImmutableArray<BuildScriptReference>>("BuildScriptInputs.DiscoverReferences.sort", () => references.Values
                    .OrderBy(reference => reference.AssemblyName, StringComparer.Ordinal)
                    .ThenBy(reference => reference.Path, StringComparer.Ordinal)
                    .ToImmutableArray());
        }

        private static void AddReference(Dictionary<string, BuildScriptReference> references, string path, Assembly? loadedAssembly)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            var fullPath = Path.GetFullPath(path);
            if (references.ContainsKey(fullPath))
                return;

            references.Add(fullPath, BuildScriptReference.Create(fullPath, loadedAssembly));
        }

        private static bool IsBuildScriptAssembly(string? assemblyName, string path)
        {
            if (string.Equals(assemblyName, AssemblyName, StringComparison.OrdinalIgnoreCase))
                return true;
            return Path.GetFileName(path).Equals(DllName, StringComparison.OrdinalIgnoreCase);
        }

        private static string HashParts(params string[] parts)
        {
            var builder = new StringBuilder();
            foreach (var part in parts)
                builder.Append(part).Append('\0');
            return HashText(builder.ToString());
        }

        private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private sealed record BuildScriptFingerprint(string Hash);

    private sealed record BuildScriptSource(
        string RelativePath,
        string FullPath,
        string Content,
        string ContentHash)
    {
        public static BuildScriptSource Create(string projectRoot, string fullPath)
        {
            fullPath = Path.GetFullPath(fullPath);
            var bytes = File.ReadAllBytes(fullPath);
            var content = Encoding.UTF8.GetString(bytes);
            var relativePath = Path.GetRelativePath(projectRoot, fullPath).Replace('\\', '/');
            var contentHash = HashBytes(SHA256.HashData(bytes));
            return new BuildScriptSource(relativePath, fullPath, content, contentHash);
        }

        public BuildScriptSourceManifest ToManifest() => new()
        {
            RelativePath = RelativePath,
            FullPath = FullPath,
            ContentHash = ContentHash,
        };
    }

    private sealed record BuildScriptReference(
        string AssemblyName,
        string Path,
        string ModuleVersionId,
        string FileHash)
    {
        public static BuildScriptReference Create(string path, Assembly? loadedAssembly)
        {
            var assemblyName = loadedAssembly?.GetName().Name ?? System.IO.Path.GetFileNameWithoutExtension(path);
            var moduleVersionId = loadedAssembly?.ManifestModule.ModuleVersionId.ToString("D") ?? "";
            var fileHash = HashFile(path);
            return new BuildScriptReference(assemblyName, path, moduleVersionId, fileHash);
        }

        public BuildScriptReferenceManifest ToManifest() => new()
        {
            AssemblyName = AssemblyName,
            Path = Path,
            ModuleVersionId = ModuleVersionId,
            FileHash = FileHash,
        };
    }

    private sealed class BuildScriptManifest
    {
        public string RuntimeSchema { get; init; } = "";
        public string CompileOptionsSchema { get; init; } = "";
        public string TargetFramework { get; init; } = "";
        public string Status { get; init; } = "";
        public string Hash { get; init; } = "";
        public string SourceHash { get; init; } = "";
        public string ReferenceHash { get; init; } = "";
        public string ProjectRoot { get; init; } = "";
        public string OutputAssembly { get; init; } = "";
        public string OutputPdb { get; init; } = "";
        public BuildScriptSourceManifest[] Sources { get; init; } = [];
        public BuildScriptReferenceManifest[] References { get; init; } = [];
        public DateTimeOffset CreatedUtc { get; init; }
        public int ProcessId { get; init; }
    }

    private sealed class BuildScriptDiagnosticsFile
    {
        public string Hash { get; init; } = "";
        public BuildScriptDiagnostic[] Diagnostics { get; init; } = [];
    }

    private sealed class BuildScriptFailure
    {
        public string Hash { get; init; } = "";
        public string Exception { get; init; } = "";
        public DateTimeOffset CreatedUtc { get; init; }
        public int ProcessId { get; init; }
    }

    private sealed class BuildScriptSourceManifest
    {
        public string RelativePath { get; init; } = "";
        public string FullPath { get; init; } = "";
        public string ContentHash { get; init; } = "";
    }

    private sealed class BuildScriptReferenceManifest
    {
        public string AssemblyName { get; init; } = "";
        public string Path { get; init; } = "";
        public string ModuleVersionId { get; init; } = "";
        public string FileHash { get; init; } = "";
    }

    private sealed class BuildScriptDiagnostic
    {
        public string Id { get; init; } = "";
        public string Severity { get; init; } = "";
        public string Message { get; init; } = "";
        public string? File { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }

        public static BuildScriptDiagnostic FromDiagnostic(Diagnostic diagnostic)
        {
            var span = diagnostic.Location.GetLineSpan();
            return new BuildScriptDiagnostic
            {
                Id = diagnostic.Id,
                Severity = diagnostic.Severity.ToString(),
                Message = diagnostic.GetMessage(),
                File = string.IsNullOrWhiteSpace(span.Path) ? null : span.Path,
                Line = span.StartLinePosition.Line + 1,
                Column = span.StartLinePosition.Character + 1,
            };
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return HashBytes(SHA256.HashData(stream));
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static class BuildScriptJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
        };
    }
}
