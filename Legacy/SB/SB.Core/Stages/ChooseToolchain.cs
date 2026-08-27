using SB.Core;
using BS = SB.BuildInstance;

namespace SB.Stages;

public class ChooseToolchain : IBuildStage
{
    public ChooseToolchain(
        bool useClangCl = true,
        WindowsSDKStrategy windowsSDKStrategy = WindowsSDKStrategy.Default)
    {
        _useClangCl = useClangCl;
        _windowsSDKStrategy = windowsSDKStrategy;
    }

    public bool Run(BuildInstance instance)
    {
        if (instance.TargetOS == OSPlatform.Emscripten)
        {
            RegisterToolchainSetups<Emscripten>(instance);
            Toolchain = instance.GetSetup<EmscriptenSetup>()!.Emscripten;
        }
        else if (instance.TargetOS == OSPlatform.Windows)
        {
            RegisterToolchainSetups<VisualStudio>(instance);
            var visualStudioSetup = instance.GetSetup<VisualStudioSetup>()!;
            visualStudioSetup.UseClangCl = _useClangCl;
            visualStudioSetup.WindowsSDKStrategy = _windowsSDKStrategy;
            Toolchain = visualStudioSetup.VisualStudio;
        }
        else if (instance.TargetOS == OSPlatform.OSX)
        {
            RegisterToolchainSetups<global::SB.Core.XCode>(instance);
            Toolchain = instance.GetSetup<XCodeSetup>()!.XCode;
        }
        else
            throw new Exception($"Unsupported target platform: {instance.TargetOS}");

        if (BS.HostOS == OSPlatform.Windows)
        {
            char DriveLetter = SourceLocation.Directory()[0];
            if (Char.IsLower(DriveLetter))
                throw new Exception($"Drive letter {DriveLetter} from source location must be upper case! You might compiled SB in git bash environment, please recompile it in cmd.exe or powershell.exe!");
        }
        return Toolchain is not null;
    }

    private static void RegisterToolchainSetups<TToolchain>(BuildInstance instance)
        where TToolchain : IToolchain
    {
        TToolchain.RegisterSetups(instance);
    }

    public IToolchain? Toolchain = null;

    private readonly bool _useClangCl;
    private readonly WindowsSDKStrategy _windowsSDKStrategy;
}
