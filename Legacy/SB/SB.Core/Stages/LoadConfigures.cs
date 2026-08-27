
using SB.Core;
using Serilog;

namespace SB.Stages;
using TargetDelegate = SB.BuildInstance.TargetDelegate;

public class LoadConfigures : IBuildStage
{
    public const string DebugConfiguration = "debug";
    public const string DebugReleaseConfiguration = "releasedbg";
    public const string ReleaseConfiguration = "release";
    public const string DynamicDebugConfiguration = "dynamicdbg";
    public static IReadOnlyList<string> ConfigurationNames { get; } =
    [
        DebugConfiguration,
        DebugReleaseConfiguration,
        ReleaseConfiguration,
        DynamicDebugConfiguration
    ];

    public LoadConfigures(string configurationName, string useDebugSymbols)
    {
        ConfigurationName = configurationName;
        UseDebugSymbols = useDebugSymbols;
    }

    public virtual bool Run(BuildInstance instance)
    {
        TargetDelegate debug = target =>
        {
            if (target is CppTarget cppTarget)
            {
                cppTarget.DebugSymbols(ParseUseDebugSymbols(true))
                    .OptimizationLevel(OptimizationLevel.None);
            }
        };
        Configurations[DebugConfiguration] = debug;

        TargetDelegate debugRelease = target =>
        {
            if (target is CppTarget cppTarget)
            {
                cppTarget.DebugSymbols(ParseUseDebugSymbols(true))
                    .Defines(Visibility.Public, "NDEBUG")
                    .OptimizationLevel(OptimizationLevel.Faster);
            }
        };
        Configurations[DebugReleaseConfiguration] = debugRelease;

        TargetDelegate release = target =>
        {
            if (target is CppTarget cppTarget)
            {
                cppTarget.DebugSymbols(ParseUseDebugSymbols(false))
                    .Defines(Visibility.Public, "NDEBUG")
                    .OptimizationLevel(OptimizationLevel.Fastest);
            }
        };
        Configurations[ReleaseConfiguration] = release;

        TargetDelegate dynamicDebug = target =>
        {
            if (target is CppTarget cppTarget)
            {
                cppTarget.DebugSymbols(false)
                    .Defines(Visibility.Public, "NDEBUG")
                    .OptimizationLevel(OptimizationLevel.Fastest)
                    .DynamicDebug(true);
            }
        };
        Configurations[DynamicDebugConfiguration] = dynamicDebug;

        return true;
    }

    public static void EnableAsan(BuildInstance instance)
    {
        if (instance.TargetOS == OSPlatform.Windows)
        {
            instance.TargetInitializationHooks += target =>
            {
                if (target is CppTarget cppTarget)
                {
                    cppTarget.Link(Visibility.Private, "clang_rt.asan_dynamic-x86_64")
                        .Link(Visibility.Private, "clang_rt.asan_dynamic_runtime_thunk-x86_64")
                        .MSVC_CXFlags(Visibility.Private, "/fsanitize=address")
                        .Defines(Visibility.Public, "_DISABLE_VECTOR_ANNOTATION")
                        .Defines(Visibility.Public, "_DISABLE_STRING_ANNOTATION");
                }
            };
        }
        else if (instance.TargetOS == OSPlatform.OSX)
        {
            instance.TargetInitializationHooks += target =>
            {
                if (target is CppTarget cppTarget)
                {
                    cppTarget.CppFlags(Visibility.Private, "-fsanitize=address")
                        .CppFlags(Visibility.Private, "-fno-omit-frame-pointer")
                        .CppFlags(Visibility.Private, "-fno-optimize-sibling-calls")
                        .AppleClang_LinkerArgs(Visibility.Private, "-fsanitize=address");
                }
            };
        }
        else
        {
            Log.Warning("AddressSanitizer is not supported on this platform yet! Please use Clang on Linux or macOS to enable ASan.");
        }
    }

    private bool ParseUseDebugSymbols(bool fallback)
    {
        if (UseDebugSymbols == "auto")
            return fallback;
        return UseDebugSymbols == "on";
    } 

    public Dictionary<string, TargetDelegate> Configurations = new();
    public string ConfigurationName = "debug";
    public string UseDebugSymbols = "auto";
}
