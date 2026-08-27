using Cli = SB.Cli;
using SB.Capabilities.BuildScripts;
using SB.Core;
using Serilog;
using Serilog.Events;
using System.Diagnostics;
using SB.Stages;

namespace SB;

public class CommandBase
{
    [Cli.Option(Name = "verbose", ShortName = 'v', Help = "Enable verbose logging", IsRequired = false)]
    public bool Verbose { get; set; } = false;

    [Cli.Option(Name = "mode", ShortName = 'm', Help = "Build configuration", IsRequired = false)]
    public string ConfigurationName { get; set; } = Engine.DefaultMode;
    
    [Cli.OptionSelectionProvider("mode")]
    public static IEnumerable<string> ConfigurationSelections()
    {
        return Stages.LoadConfigures.ConfigurationNames;
    }

    [Cli.Option(Name = "sha-depend", Help = "Use SHA instead of DateTime for dependency checking", IsRequired = false)]
    public bool UseShaDepend { get; set; } = false;

    [Cli.Option(Name = "filter-tags", Help = "Only build root targets that have at least one of these tags", IsRequired = false)]
    public string FilterTags { get; set; } = "";

    [Cli.Option(Name = "exclude-tags", Help = "Skip root targets that have any of these tags", IsRequired = false)]
    public string ExcludeTags { get; set; } = "";

    [Cli.Option(Name = "toolchain", Help = "Toolchain to use", IsRequired = false, Selections = ["msvc", "clang-cl", "clang", "apple-clang", "emscripten"])]
    public string ToolchainName { get; set; } = Engine.DefaultToolchain;

    [Cli.Option(Name = "wasm-threads", Help = "Enable Emscripten pthreads", IsRequired = false, Selections = ["on", "off"])]
    public string WasmThreads { get; set; } = "on";

    [Cli.Option(Name = "proxy", Help = "Set HTTP proxy for downloads")]
    public string Proxy { get; set; } = "";

    [Cli.Option(Name = "plat", ShortName = 'p', Help = "Set build platform")]
    public string Platform { get; set; } = "";

    [Cli.Option(Name = "arch", ShortName = 'a', Help = "Set build architecture")]
    public string Architecture { get; set; } = "";

    [Cli.Option(Name = "symbols", Help = "Set build directory", Selections = ["auto", "on", "off"])]
    public string symbols { get; set; } = "auto";

    [Cli.ExecCmd]
    public int Exec()
    {
        BuildTrace.Mark("CommandBase.Exec.enter");
        Stopwatch timer = Stopwatch.StartNew();

        BuildTrace.Measure("CommandBase.SetupHostSettings", SetupHostSettings);

        // run custom exec
        int returnCode;
        using (BuildTrace.Scope("CommandBase.OnExecute"))
        {
            returnCode = OnExecuteAsync().GetAwaiter().GetResult();
        }

        // stop and dump counters
        timer.Stop();
        if (DumpCounters)
        {
            Log.Information($"Total: {timer.ElapsedMilliseconds / 1000.0f}s");
            Log.Information($"Execution Total: {timer.ElapsedMilliseconds / 1000.0f}s");
        }

        Log.CloseAndFlush();

        BuildTrace.Mark("CommandBase.Exec.exit", $"return={returnCode} elapsed={timer.ElapsedMilliseconds}ms");
        return returnCode;
    }

    private void SetupHostSettings()
    {
        // setup log level
        Logging.InitializeLogger(Verbose ? LogEventLevel.Verbose : LogEventLevel.Information);

        // Emscripten: --toolchain and --plat are aliases. Setting either one
        // implies the other, and --arch defaults to wasm32.
        bool wantsEmscripten = string.Equals(ToolchainName, "emscripten", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(Platform, "emscripten", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(Platform, "wasm", StringComparison.OrdinalIgnoreCase);
        if (wantsEmscripten)
        {
            ToolchainName = "emscripten";
            Platform = "emscripten";
            if (string.IsNullOrWhiteSpace(Architecture))
                Architecture = "wasm32";
        }

    }

    private static OSPlatform ParsePlatform(string platform)
    {
        return platform.Trim().ToLowerInvariant() switch
        {
            "windows" or "win" => OSPlatform.Windows,
            "linux" => OSPlatform.Linux,
            "osx" or "macos" or "macosx" => OSPlatform.OSX,
            "emscripten" or "wasm" => OSPlatform.Emscripten,
            _ => throw new ArgumentException($"Invalid platform: {platform}")
        };
    }

    private static SB.Core.Architecture ParseArchitecture(string arch)
    {
        return arch.Trim().ToLowerInvariant() switch
        {
            "x86" or "i386" => SB.Core.Architecture.X86,
            "x64" or "x86_64" or "amd64" => SB.Core.Architecture.X64,
            "arm64" or "aarch64" => SB.Core.Architecture.ARM64,
            "wasm32" => SB.Core.Architecture.Wasm32,
            _ => throw new ArgumentException($"Invalid architecture: {arch}")
        };
    }

    protected BuildInstance CreateBuildInstance()
    {
        using (BuildTrace.Scope("CommandBase.CreateBuildInstance"))
        {
            var instance = new BuildInstance();
            InitializeBuildInstance(instance);
            return instance;
        }
    }

    protected void InitializeBuildInstance(BuildInstance instance)
    {
        using var trace = BuildTrace.Scope("CommandBase.InitializeBuildInstance");

        using (BuildTrace.Scope("CommandBase.InitializeBuildInstance.parse_target"))
        {
            instance.TargetOS = string.IsNullOrWhiteSpace(Platform) ? BuildInstance.HostOS : ParsePlatform(Platform);
            instance.TargetArch = string.IsNullOrWhiteSpace(Architecture) ? BuildInstance.HostArch : ParseArchitecture(Architecture);
            if (!TryParseBoolean(WasmThreads, out var emscriptenThreads))
                throw new ArgumentException($"Invalid wasm thread mode: {WasmThreads}");
            instance.EmscriptenThreads = emscriptenThreads;
        }

        using (BuildTrace.Scope("CommandBase.InitializeBuildInstance.add_download_setup"))
        {
            instance.AddSetup<DownloadSetup>().HttpProxy = Proxy;
        }

        Log.Information("Build start with configuration: {Configuration}", ConfigurationName.ToLowerInvariant());
        Log.Information("Build start with platform: {Platform}, architecture: {Architecture}", instance.TargetOS, instance.TargetArch);
        if (instance.TargetOS == OSPlatform.Emscripten)
            Log.Information("Emscripten pthreads: {State}", instance.EmscriptenThreads ? "on" : "off");

        if (!string.IsNullOrEmpty(Proxy))
            Log.Information("Setting HTTP proxy to {Proxy}", Proxy);

        BuildTrace.Measure("CommandBase.InitializeBuildInstance.BuildScriptRuntime.EnsureLoaded", BuildScriptRuntime.EnsureLoaded);

        instance.RunStage<Stages.ChooseToolchain>(ResolveUseClangCl(), WindowsSDKStrategy.Default);
        instance.RunStage<Stages.LoadConfigures>(ConfigurationName.ToLowerInvariant(), symbols);
        ApplyUseProfileOverride(instance);
        instance.RunStage<Stages.PrepareEngineDirectoriesStage>();
        instance.RunStage<Stages.PrepareEngineDatabasesStage>(UseShaDepend);
        instance.RunStage<Stages.LoadEngineTargets>();
        BuildTrace.Measure("CommandBase.InitializeBuildInstance.RunSetups", instance.RunSetups);
    }

    private static void ApplyUseProfileOverride(BuildInstance instance)
    {
        var value = Environment.GetEnvironmentVariable("SB_USE_PROFILE");
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (TryParseBoolean(value, out var useProfile))
        {
            instance.UseProfile = useProfile;
            Log.Information("Build profiling override: SB_USE_PROFILE={Value}, UseProfile={UseProfile}", value, useProfile);
            return;
        }

        Log.Warning("Ignoring invalid SB_USE_PROFILE={Value}; use 1/true/on/yes or 0/false/off/no.", value);
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                result = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    protected IReadOnlyList<string> ParsedFilterTags => ParseTagList(FilterTags);
    protected IReadOnlyList<string> ParsedExcludeTags => ParseTagList(ExcludeTags);

    protected IEnumerable<Target> ApplyRootTagFilters(IEnumerable<Target> candidates)
    {
        var filterTags = ParsedFilterTags;
        var excludeTags = ParsedExcludeTags;
        foreach (var target in candidates)
        {
            if (filterTags.Count > 0 && !filterTags.Any(tag => TargetHasTag(target, tag)))
                continue;
            if (excludeTags.Count > 0 && excludeTags.Any(tag => TargetHasTag(target, tag)))
                continue;
            yield return target;
        }
    }

    internal static bool TargetHasTag(Target target, string tag)
    {
        var resolvedTag = ResolveTargetTagName(tag);
        return target.HasTag(resolvedTag);
    }

    protected static bool TargetPositiveBuild(Target target)
    {
        return target.IsPositiveBuild();
    }

    protected int RunBuildForAllTargets(BuildInstance instance, bool onlyPositive = false)
    {
        var targets = onlyPositive
            ? instance.AllTargets.Values.Where(TargetPositiveBuild)
            : instance.AllTargets.Values;

        var rootTargetNames = ApplyRootTagFilters(targets)
            .Select(target => target.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (rootTargetNames.Length == 0)
        {
            Log.Warning("No root targets selected.");
            return 0;
        }

        return instance.RunBuildTargets(rootTargetNames);
    }

    private static IReadOnlyList<string> ParseTagList(string tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
            return [];
        return tags
            .Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ResolveTargetTagName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveTargetTagName(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return tag;

        return NormalizeTagAlias(tag);
    }

    private static string NormalizeTagAlias(string tag)
    {
        return tag.Trim().ToLowerInvariant() switch
        {
            "tool" or "tools" => TargetTags.Tool,
            "test" or "tests" => TargetTags.Test,
            "bench" or "benches" => TargetTags.Bench,
            "dev" or "devtime" or "dev-time" => TargetTags.DevTime,
            "package" or "packages" => TargetTags.Package,
            "runtime" => TargetTags.Runtime,
            "core" => TargetTags.Core,
            _ => tag.Trim()
        };
    }

    public virtual Task<int> OnExecuteAsync()
    {
        return Task.FromResult(OnExecute());
    }

    public virtual int OnExecute()
    {
        return 0;
    }

    protected static int GetEmitterElapsedMilliseconds(BuildInstance instance, string emitterName)
    {
        return instance.GetTaskEmitter(emitterName)?.ElapsedMilliseconds ?? 0;
    }

    protected static IToolchain GetToolchain(BuildInstance instance) => instance.GetStage<ChooseToolchain>()!.Toolchain!;
    protected virtual bool DumpCounters => true;

    private bool ResolveUseClangCl() =>
        !string.Equals(ToolchainName, "msvc", StringComparison.OrdinalIgnoreCase);
}
