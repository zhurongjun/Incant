using SB.Core;
using Serilog;

namespace SB
{
    public static partial class Engine
    {
        public static string DefaultMode = "debug";
        public static string DefaultToolchain = OperatingSystem.IsWindows() ? "clang-cl" : "clang";

        public static void AddCppPreparationEmitters(this BuildInstance Instance)
        {
            Instance.AddTaskEmitter("Cpp.UnityBuild", new UnityBuildEmitter())
                .AddDependency("Build.BeforeBuild", DependencyModel.PerTarget)
                .AddDependency("Build.BeforeBuild", DependencyModel.ExternalTarget);
        }

        public static void AddBeforeBuildEmitter(this BuildInstance Instance)
        {
            if (Instance.GetTaskEmitter("Build.BeforeBuild") is not null)
                return;

            Instance.AddTaskEmitter("Build.BeforeBuild", new BeforeBuildEmitter())
                .AddDependency("Build.BeforeBuild", DependencyModel.ExternalTarget);
        }

        public static void AddEngineTaskEmitters(this BuildInstance Instance, IToolchain Toolchain)
        {
            Log.Verbose("Add Engine Task Emitters... ");

            Instance.AddTaskEmitter("Utils.CopyFiles", new CopyFilesEmitter())
                .AddDependency("Build.BeforeBuild", DependencyModel.PerTarget)
                .AddDependency("Build.BeforeBuild", DependencyModel.ExternalTarget);

            Instance.AddTaskEmitter("Cpp.PCH", new PCHEmitter(Toolchain))
                .AddDependency("Build.BeforeBuild", DependencyModel.PerTarget)
                .AddDependency("Build.BeforeBuild", DependencyModel.ExternalTarget);

            Instance.AddTaskEmitter("Cpp.Compile", new CppCompileEmitter(Toolchain))
                .AddDependency("Build.BeforeBuild", DependencyModel.PerTarget)
                .AddDependency("Build.BeforeBuild", DependencyModel.ExternalTarget)
                .AddDependency("Cpp.UnityBuild", DependencyModel.PerTarget)
                .AddDependency("Cpp.PCH", DependencyModel.PerTarget)
                .AddDependency("Cpp.PCH", DependencyModel.ExternalTarget);

            if (Instance.TargetOS == OSPlatform.Windows)
            {
                Instance.AddTaskEmitter("Rc.Compile", new RcCompileEmitter(Toolchain))
                    .AddDependency("Build.BeforeBuild", DependencyModel.PerTarget)
                    .AddDependency("Build.BeforeBuild", DependencyModel.ExternalTarget);
            }

            Instance.AddTaskEmitter("Cpp.Link", new CppLinkEmitter(Toolchain))
                .AddDependency("Build.BeforeBuild", DependencyModel.PerTarget)
                .AddDependency("Build.BeforeBuild", DependencyModel.ExternalTarget)
                .AddDependency("Cpp.Link", DependencyModel.ExternalTarget)
                .AddDependency("Cpp.Compile", DependencyModel.PerTarget)
                .AddDependency("Rc.Compile", DependencyModel.PerTarget);

            Instance.AddTaskEmitter("Install.Artifact", new InstallArtifactEmitter())
                .AddDependency("Build.BeforeBuild", DependencyModel.PerTarget)
                .AddDependency("Build.BeforeBuild", DependencyModel.ExternalTarget)
                .AddDependency("Cpp.Link", DependencyModel.PerTarget);
        }

        public static void AddCompileCommandsEmitter(
            this BuildInstance Instance,
            IToolchain Toolchain)
        {
            Instance.AddTaskEmitter("Cpp.CompileCommands", new CompileCommandsEmitter(Toolchain));

            Instance.GetTaskEmitter("Cpp.UnityBuild")
                ?.AddDependency("Cpp.CompileCommands", DependencyModel.PerTarget);
        }
    }
}
