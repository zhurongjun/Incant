using Serilog;
using System.Reflection;
using SB.Core;
using BS = SB.BuildInstance;

namespace SB.Stages;

public class LoadEngineTargets : IBuildStage
{
    public LoadEngineTargets()
    {
    }

    public virtual bool Run(BuildInstance instance)
    {
        Log.Verbose("Load Targets... ");
        instance.TargetInitializationHooks += target =>
        {
            if (target is CppTarget cppTarget)
            {
                cppTarget.CppVersion("20");

                if (target.IsFromPackage)
                {
                    var buildDirs = instance.GetStage<Stages.PrepareBuildDirectoriesStage>()!;
                    var configures = instance.GetStage<Stages.LoadConfigures>()!;
                    var buildDirectory = Path.Combine(buildDirs.BuildDir, $"{instance.TargetOS}-{instance.TargetArch}-{configures.ConfigurationName}");
                    cppTarget.LinkDirs(Visibility.Public, buildDirectory)
                        .InstallArtifact();
                }
            }
        };

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var types = assemblies.SelectMany(GetLoadableTypes);
        var scripts = types.Where(IsTargetScript);
        foreach (var script in scripts)
        {
            var previousTargets = instance.AllTargets.Values.ToHashSet();
            var targetScript = script.GetCustomAttribute<TargetScript>()!;
            var register = script.GetMethod("Register", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (register is null || register.GetParameters().Length != 1 || register.GetParameters()[0].ParameterType != typeof(BuildInstance) || register.ReturnType != typeof(void))
                throw new TaskFatalError($"Target script {script.FullName} must define a static Register(BuildInstance) method.");

            try
            {
                register.Invoke(null, [instance]);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is TaskFatalError fatal)
            {
                throw fatal;
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                throw new TaskFatalError($"Target script {script.FullName}.Register failed.", inner.ToString());
            }
            var newTargets = instance.AllTargets.Values.Except(previousTargets);
            foreach (var newTarget in newTargets)
            {
                newTarget.AddTags(targetScript.Tags);
            }
        }
        return true;
    }

    private static bool IsTargetScript(Type type)
    {
        return type.GetCustomAttribute<TargetScript>() is not null;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var loaderMessages = ex.LoaderExceptions
                .Where(loaderException => loaderException is not null)
                .Select(loaderException => loaderException!.Message)
                .ToArray();
            if (string.Equals(assembly.GetName().Name, "SB.BuildScripts", StringComparison.Ordinal))
            {
                var message = loaderMessages.Length == 0
                    ? $"Failed to load target script types from {assembly.FullName}."
                    : $"Failed to load target script types from {assembly.FullName}:{Environment.NewLine}{string.Join(Environment.NewLine, loaderMessages)}";
                throw new TaskFatalError(message);
            }

            foreach (var loaderMessage in loaderMessages)
                Log.Warning("Failed to load type from {Assembly}: {Message}", assembly.FullName, loaderMessage);
            return ex.Types.Where(type => type is not null)!;
        }
    }
}
