using System.Reflection;
using SB.Core;

namespace SB;

public interface IBuildStage
{
    public bool Run(BuildInstance instance);
    public void Shutdown(BuildInstance instance) { }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class BeforeStageAttribute : Attribute
{
    public Type StageType { get; private set; }

    public BeforeStageAttribute(Type stageType)
    {
        if (!typeof(IBuildStage).IsAssignableFrom(stageType))
            throw new ArgumentException($"Type {stageType.Name} must implement IBuildStage", nameof(stageType));
        StageType = stageType;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class AfterStageAttribute : Attribute
{
    public Type StageType { get; private set; }

    public AfterStageAttribute(Type stageType)
    {
        if (!typeof(IBuildStage).IsAssignableFrom(stageType))
            throw new ArgumentException($"Type {stageType.Name} must implement IBuildStage", nameof(stageType));
        StageType = stageType;
    }
}

public delegate void StageCallback(BuildInstance instance);

public partial class BuildInstance
{
    private readonly Dictionary<Type, List<StageCallback>> _beforeStageCallbacks = new();
    private readonly Dictionary<Type, List<StageCallback>> _afterStageCallbacks = new();
    private readonly HashSet<Type> _completedStages = new();
    private readonly List<IBuildStage> _buildStages = new();
    private bool _callbacksCollected = false;

    private void CollectStageCallbacksImpl()
    {
        if (_callbacksCollected)
            return;
        _callbacksCollected = true;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        foreach (var attr in method.GetCustomAttributes<BeforeStageAttribute>())
                        {
                            if (method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(BuildInstance) && method.ReturnType == typeof(void))
                                BeforeStage(attr.StageType, instance => method.Invoke(null, [instance]));
                            else
                                throw new InvalidOperationException($"BeforeStage method {type.FullName}.{method.Name} must accept BuildInstance and return void");
                        }

                        foreach (var attr in method.GetCustomAttributes<AfterStageAttribute>())
                        {
                            if (method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(BuildInstance) && method.ReturnType == typeof(void))
                                AfterStage(attr.StageType, instance => method.Invoke(null, [instance]));
                            else
                                throw new InvalidOperationException($"AfterStage method {type.FullName}.{method.Name} must accept BuildInstance and return void");
                        }
                    }
                }
            }
            catch (ReflectionTypeLoadException)
            {
            }
        }
    }

    public void BeforeStage<TStage>(StageCallback callback) where TStage : class, IBuildStage =>
        BeforeStage(typeof(TStage), callback);

    public void BeforeStage(Type stageType, StageCallback callback)
    {
        if (_completedStages.Contains(stageType))
            return;

        if (!_beforeStageCallbacks.TryGetValue(stageType, out var callbacks))
        {
            callbacks = new List<StageCallback>();
            _beforeStageCallbacks[stageType] = callbacks;
        }
        callbacks.Add(callback);
    }

    public void AfterStage<TStage>(StageCallback callback) where TStage : class, IBuildStage =>
        AfterStage(typeof(TStage), callback);

    public void AfterStage(Type stageType, StageCallback callback)
    {
        if (_completedStages.Contains(stageType))
        {
            callback(this);
            return;
        }

        if (!_afterStageCallbacks.TryGetValue(stageType, out var callbacks))
        {
            callbacks = new List<StageCallback>();
            _afterStageCallbacks[stageType] = callbacks;
        }
        callbacks.Add(callback);
    }

    private void InvokeBeforeStageCallbacks(Type stageType)
    {
        if (_beforeStageCallbacks.TryGetValue(stageType, out var callbacks))
        {
            foreach (var callback in callbacks)
                callback(this);
            _beforeStageCallbacks.Remove(stageType);
        }
    }

    private void InvokeAfterStageCallbacks(Type stageType)
    {
        _completedStages.Add(stageType);

        if (_afterStageCallbacks.TryGetValue(stageType, out var callbacks))
        {
            foreach (var callback in callbacks)
                callback(this);
            _afterStageCallbacks.Remove(stageType);
        }
    }

    public bool RunStage<T>(params object[] args) where T : class, IBuildStage
    {
        var stageName = typeof(T).Name;
        using var trace = BuildTrace.Scope($"Stage.{stageName}");

        BuildTrace.Measure($"Stage.{stageName}.CollectStageCallbacks", CollectStageCallbacksImpl);

        var existed = GetStage<T>();
        if (existed is not null)
        {
            BuildTrace.Mark($"Stage.{stageName}.cached");
            return true;
        }

        BuildTrace.Measure($"Stage.{stageName}.BeforeCallbacks", () => InvokeBeforeStageCallbacks(typeof(T)));

        var newStage = BuildTrace.Measure<T?>($"Stage.{stageName}.CreateInstance", () => Activator.CreateInstance(typeof(T), args) as T);
        _buildStages.Add(newStage!);
        var result = BuildTrace.Measure<bool>($"Stage.{stageName}.Run", () => newStage!.Run(this));

        BuildTrace.Measure($"Stage.{stageName}.AfterCallbacks", () => InvokeAfterStageCallbacks(typeof(T)));

        return result;
    }

    public bool UnloadStage<T>() where T : class, IBuildStage
    {
        var stage = GetStage<T>();
        if (stage is null)
            return false;

        stage.Shutdown(this);
        _completedStages.Remove(typeof(T));
        return _buildStages.Remove(stage);
    }

    public void UnloadAllStages()
    {
        for (int i = _buildStages.Count - 1; i >= 0; i--)
            _buildStages[i].Shutdown(this);

        _buildStages.Clear();
        _completedStages.Clear();
        _beforeStageCallbacks.Clear();
        _afterStageCallbacks.Clear();
        _callbacksCollected = false;
    }

    public T? GetStage<T>() where T : class
    {
        foreach (var stage in _buildStages)
        {
            if (stage is T typed)
                return typed;
        }
        return null;
    }
}
