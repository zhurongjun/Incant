using SB.Core;
using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SB
{
    public partial class BuildInstance
    {
        public delegate void TargetDelegate(Target buildTarget);

        private static TargetDelegate CreateDefaultTargetInitializationHooks()
        {
            return new TargetDelegate(buildTarget =>
            {
                buildTarget.SetAttribute(new RcCompileAttribute());
                buildTarget.SetAttribute(new CppCompileAttribute());
                buildTarget.SetAttribute(new CppLinkAttribute());
            });
        }

        public BuildInstance()
        {
            using (BuildTrace.Scope("BuildInstance.ctor"))
            {
                _targetTaskScheduler = TaskManager.BuildSchedulerPool.ActivateNewQueue(0);
                _fileTaskScheduler = TaskManager.BuildSchedulerPool.ActivateNewQueue(1);
                _targetInitializationHooks = CreateDefaultTargetInitializationHooks();
            }
        }

        public Dictionary<string, Target> AllTargets => _allTargets;
        public ConcurrentBag<IArtifact> Artifacts => _artifacts;
        public DependencyDatabaseCache DependencyDatabaseCache { get; } = new();
        public TaskScheduler TargetTaskScheduler => _targetTaskScheduler;
        public TaskScheduler FileTaskScheduler => _fileTaskScheduler;
        internal Dictionary<string, Target> MutableTargets => _allTargets;
        protected Dictionary<string, Package> AllPackages => _allPackages;
        public IReadOnlyDictionary<string, Package> Packages => _allPackages;
        public TargetDelegate TargetInitializationHooks
        {
            get => _targetInitializationHooks;
            set => _targetInitializationHooks = value;
        }
        public bool UseProfile
        {
            get => _useProfile;
            set => _useProfile = value;
        }
        public bool EmscriptenThreads
        {
            get => _emscriptenThreads;
            set => _emscriptenThreads = value;
        }

        private readonly ConcurrentBag<IArtifact> _artifacts = new();
        private readonly Dictionary<string, TaskEmitter> _taskEmitters = new();
        private readonly Dictionary<string, Target> _allTargets = new();
        private readonly Dictionary<string, Package> _allPackages = new();
        private readonly ConcurrentDictionary<BuildTaskKey, Task<bool>> _runningTasks = new();
        private readonly ConcurrentQueue<TaskFatalError> _fatalErrors = new();
        private readonly TaskScheduler _targetTaskScheduler;
        private readonly TaskScheduler _fileTaskScheduler;
        private static readonly AsyncLocal<CancellationToken> _processCancellation = new();

        private TargetDelegate _targetInitializationHooks;
        private CancellationTokenSource _buildCancellation = new();
        private bool _firstRun = true;
        private volatile bool _stopAll = false;
        private int _fatalErrorCount = 0;
        private uint _allTaskCounter = 0;
        private uint _fileTaskCounter = 0;
        private Architecture _targetArch = HostArch;
        private OSPlatform _targetOS = HostOS;
        private bool _useProfile = true;
        private bool _emscriptenThreads = true;

        public CppTarget CppTarget(string name, [CallerFilePath] string? location = null, [CallerLineNumber] int lineNumber = 0)
        {
            if (AllTargets.TryGetValue(name, out var _))
                throw new ArgumentException($"Target with name {name} already exists! Name should be unique to every target!");

            var newTarget = new CppTarget(this, name, false, location!, lineNumber);
            if (!AllTargets.TryAdd(name, newTarget))
                throw new ArgumentException($"Failed to add target with name {name}! Are you adding same targets in parallel?");

            return newTarget;
        }

        public Package Package(string name)
        {
            if (AllPackages.TryGetValue(name, out var _))
                throw new ArgumentException($"Package with name {name} already exists! Name should be unique to every package!");

            var newPackage = new Package(this, name);
            if (!AllPackages.TryAdd(name, newPackage))
                throw new ArgumentException($"Failed to add package with name {name}! Are you adding same packages in parallel?");

            return newPackage;
        }

        public Target? GetTarget(string name) => AllTargets.TryGetValue(name, out var found) ? found : null;

        public Package GetPackage(string name) =>
            AllPackages.TryGetValue(name, out var found) ? found : throw new ArgumentException($"Package with name {name} not found!");

        public TEmitter AddTaskEmitter<TEmitter>(string name, TEmitter emitter)
            where TEmitter : TaskEmitter
        {
            if (_taskEmitters.TryGetValue(name, out var _))
                throw new ArgumentException($"Emitter with name {name} already exists! Name should be unique to every emitter!");

            _taskEmitters.Add(name, emitter);
            emitter.Attach(this, name);
            emitter.RegisterSetups(this);
            return emitter;
        }

        public TaskEmitter? GetTaskEmitter(string name) =>
            _taskEmitters.TryGetValue(name, out var found) ? found : null;

        public int RunBuild(string? singleTargetName = null)
        {
            return RunBuildWithTargets(string.IsNullOrEmpty(singleTargetName) ? null : [singleTargetName]);
        }

        public int RunBuildTargets(params string[] targetNames)
        {
            return RunBuildWithTargets(targetNames.Length == 0 ? null : targetNames);
        }

        private int RunBuildWithTargets(IReadOnlyCollection<string>? targetNames)
        {
            using var trace = BuildTrace.Scope("BuildInstance.RunBuildWithTargets");
            BuildTrace.Mark("BuildInstance.RunBuildWithTargets.targets", targetNames is null ? "<positive-build>" : string.Join(",", targetNames));
            int errorCount = 0;
            BuildTrace.Measure("BuildInstance.PrepareTaskManagerForRun", PrepareTaskManagerForRun);
            try
            {
                Log.Verbose("Run Build... ");
                using (Profiler.BeginZone("RunBuild", color: (uint)Profiler.ColorType.WebPurple))
                {
                    BuildTrace.Measure("BuildInstance.RunBuildImpl", () => RunBuildImpl(targetNames));
                }
            }
            catch (OperationCanceledException)
            {
                ForceQuitManagedTasks();
            }
            catch (TaskFatalError fatal)
            {
                RecordFatal(fatal);
            }
            finally
            {
                TaskFatalError? fatal = null;
                const int maxDisplayCount = 5;
                while (_fatalErrors.TryDequeue(out fatal))
                {
                    if (errorCount == 0)
                        Log.Error("{FatalTidy} Detail:\n{FatalMessage}", fatal.Tidy, fatal.Message);
                    else if (errorCount < maxDisplayCount)
                        Log.Error("{FatalTidy}", fatal.Tidy);
                    errorCount += 1;
                }
                _runningTasks.Clear();
            }
            return errorCount;
        }

        private void PrepareTaskManagerForRun()
        {
            if (_buildCancellation.IsCancellationRequested)
            {
                _buildCancellation.Dispose();
                _buildCancellation = new CancellationTokenSource();
            }
            _stopAll = false;
            _fatalErrorCount = 0;
            _runningTasks.Clear();
            while (_fatalErrors.TryDequeue(out _))
            {
            }
        }

        private async Task RunManagedTask(BuildTaskKey taskKey, Func<Task<bool>> function, TaskScheduler? scheduler = null)
        {
            if (_runningTasks.TryGetValue(taskKey, out var _))
                throw new ArgumentException($"Task with key {taskKey} already exists! Task keys should be unique to every task!");

            var newTask = new Task<Task<bool>>(
                async () =>
                {
                    var previousCancellation = _processCancellation.Value;
                    _processCancellation.Value = _buildCancellation.Token;
                    try
                    {
                        if (_stopAll)
                            return await Task.FromResult(false);
                        return await function();
                    }
                    catch (OperationCanceledException) when (_buildCancellation.IsCancellationRequested)
                    {
                        return await Task.FromResult(false);
                    }
                    catch (TaskFatalError fatal)
                    {
                        RecordFatal(fatal);
                        return await Task.FromResult(false);
                    }
                    finally
                    {
                        _processCancellation.Value = previousCancellation;
                    }
                }, TaskCreationOptions.AttachedToParent);
            newTask.Start(scheduler ?? TaskScheduler.Default);

            if (!_runningTasks.TryAdd(taskKey, await newTask))
                throw new ArgumentException($"Failed to add task with key {taskKey}! Are you adding same tasks in parallel?");
        }

        private void WaitAllManagedTasks(IEnumerable<Task> tasks) => Task.WaitAll(tasks.ToArray());

        private void WaitAllManagedTasks() => WaitAllManagedTasks(_runningTasks.Values.Cast<Task>());

        private void ForceQuitManagedTasks()
        {
            _stopAll = true;
            _buildCancellation.Cancel();
            WaitAllManagedTasks(_runningTasks.Values.Cast<Task>());
        }

        private void RecordFatal(TaskFatalError fatal)
        {
            _fatalErrors.Enqueue(fatal);
            _stopAll = true;
            if (Interlocked.Increment(ref _fatalErrorCount) != 1)
                return;

            _buildCancellation.Cancel();
        }

        private struct PlanKey
        {
            public required string Target { get; init; }
            public required string Emitter { get; init; }
        }

        private class TaskPlan
        {
            public required PlanKey Key { get; set; }
            public required Target Target { get; set; }
            public required TaskEmitter Emitter { get; set; }
            public HashSet<PlanKey> DirectDependencies { get; set; } = new();
        }

        private void GlobDependencies(Target targetToGlob, Dictionary<string, Target> targetsToBuild)
        {
            var directDependencies = targetToGlob.Dependencies.Select(dependency => AllTargets[dependency]);
            foreach (var directDependency in directDependencies)
            {
                if (directDependency.Dependencies.Count > 0)
                    GlobDependencies(directDependency, targetsToBuild);

                targetsToBuild.TryAdd(directDependency.Name, directDependency);
            }
        }

        private void AddResolvedPackageTargets(Dictionary<string, Target> packageTargets)
        {
            foreach (var packageTarget in packageTargets)
                AllTargets.TryAdd(packageTarget.Key, packageTarget.Value);
        }

        private IEnumerable<Target> GetDirectTargetDependencies(Target target)
        {
            var dependencyNames = target.PublicDependencies
                .Concat(target.PrivateDependencies)
                .Concat(target.InterfaceDependencies)
                .Distinct(StringComparer.Ordinal);

            foreach (var dependencyName in dependencyNames)
            {
                if (!AllTargets.TryGetValue(dependencyName, out var dependency))
                    throw new TaskFatalError($"Target {target.Name}: Dependency {dependencyName} does not exist!");

                yield return dependency;
            }
        }

        private void ResolveReachablePackages(
            Target target,
            Dictionary<string, Target> packageTargets,
            HashSet<string> visitedTargets)
        {
            if (!visitedTargets.Add(target.Name))
                return;

            target.ResolvePackages(ref packageTargets);
            AddResolvedPackageTargets(packageTargets);

            foreach (var dependency in GetDirectTargetDependencies(target))
                ResolveReachablePackages(dependency, packageTargets, visitedTargets);
        }

        private void ResolvePackages(IReadOnlyCollection<string>? targetNames)
        {
            Log.Verbose("Resolving Packages... ");
            using (Profiler.BeginZone("ResolvePackages", color: (uint)Profiler.ColorType.Yellow))
            {
                Dictionary<string, Target> packageTargets = new();
                if (targetNames is { Count: > 0 })
                {
                    BuildTrace.Measure("RunBuild.ResolvePackages", () =>
                    {
                        HashSet<string> visitedTargets = new(StringComparer.Ordinal);
                        foreach (var targetName in targetNames.Distinct(StringComparer.Ordinal))
                        {
                            if (string.IsNullOrWhiteSpace(targetName))
                                throw new TaskFatalError("Target name cannot be empty.");

                            var rootTarget = GetTarget(targetName);
                            if (rootTarget is null)
                                throw new TaskFatalError($"Target {targetName} does not exist!");

                            ResolveReachablePackages(rootTarget, packageTargets, visitedTargets);
                        }
                        BuildTrace.Mark("RunBuild.ResolvePackages.counts", $"package_targets={packageTargets.Count} all_targets={AllTargets.Count}");
                    });
                    return;
                }

                if (!_firstRun)
                    return;

                BuildTrace.Measure("RunBuild.ResolvePackages", () =>
                {
                    foreach (var targetEntry in AllTargets)
                        targetEntry.Value.ResolvePackages(ref packageTargets);

                    AllTargets.AddRange(packageTargets);
                    _firstRun = false;
                    BuildTrace.Mark("RunBuild.ResolvePackages.counts", $"package_targets={packageTargets.Count} all_targets={AllTargets.Count}");
                });
            }
        }

        private void ResolveReachableDependencies(Target target, HashSet<string> visitedTargets)
        {
            if (!visitedTargets.Add(target.Name))
                return;

            foreach (var dependency in GetDirectTargetDependencies(target))
                ResolveReachableDependencies(dependency, visitedTargets);

            target.ResolveDependencies();
        }

        private void ResolveDependencies(IReadOnlyCollection<string>? targetNames)
        {
            Log.Verbose("Resolving Dependencies... ");
            using (Profiler.BeginZone("ResolveDependencies", color: (uint)Profiler.ColorType.Blue2))
            {
                BuildTrace.Measure("RunBuild.ResolveDependencies", () =>
                {
                    if (targetNames is { Count: > 0 })
                    {
                        HashSet<string> visitedTargets = new(StringComparer.Ordinal);
                        foreach (var targetName in targetNames.Distinct(StringComparer.Ordinal))
                        {
                            if (string.IsNullOrWhiteSpace(targetName))
                                throw new TaskFatalError("Target name cannot be empty.");

                            var rootTarget = GetTarget(targetName);
                            if (rootTarget is null)
                                throw new TaskFatalError($"Target {targetName} does not exist!");

                            ResolveReachableDependencies(rootTarget, visitedTargets);
                        }
                        BuildTrace.Mark("RunBuild.ResolveDependencies.counts", $"reachable_targets={visitedTargets.Count} all_targets={AllTargets.Count}");
                        return;
                    }

                    foreach (var targetEntry in AllTargets)
                        targetEntry.Value.ResolveDependencies();
                });
            }
        }

        private void RunBuildImpl(IReadOnlyCollection<string>? targetNames = null)
        {
            ResolvePackages(targetNames);

            // some target setups are loaded from package scope
            BuildTrace.Measure("RunBuild.RunSetups", RunSetups);

            ResolveDependencies(targetNames);

            Dictionary<string, Target> targetsToBuild = new();
            BuildTrace.Measure("RunBuild.SelectTargets", () =>
            {
                if (targetNames is { Count: > 0 })
                {
                    foreach (var targetName in targetNames.Distinct(StringComparer.Ordinal))
                    {
                        if (string.IsNullOrWhiteSpace(targetName))
                            throw new TaskFatalError("Target name cannot be empty.");

                        var rootTarget = GetTarget(targetName);
                        if (rootTarget is null)
                            throw new TaskFatalError($"Target {targetName} does not exist!");

                        GlobDependencies(rootTarget, targetsToBuild);
                        targetsToBuild.TryAdd(rootTarget.Name, rootTarget);
                    }
                }
                else
                {
                    foreach (var rootTarget in AllTargets.Values.Where(target => target.IsPositiveBuild()))
                    {
                        GlobDependencies(rootTarget, targetsToBuild);
                        targetsToBuild.TryAdd(rootTarget.Name, rootTarget);
                    }
                }
                BuildTrace.Mark("RunBuild.SelectTargets.count", targetsToBuild.Count.ToString());
            });

            using (Profiler.BeginZone("CallAfterLoads", color: (uint)Profiler.ColorType.Green))
            {
                BuildTrace.Measure("RunBuild.CallAfterLoads", () =>
                {
                    foreach (var targetEntry in targetsToBuild)
                        targetEntry.Value.CallAllActions(targetEntry.Value.AfterLoadActions);
                });
            }

            Log.Verbose("Resolving Arguments... ");
            using (Profiler.BeginZone("ResolveArguments", color: (uint)Profiler.ColorType.Pink))
            {
                BuildTrace.Measure("RunBuild.ResolveArguments", () =>
                {
                    Parallel.ForEach(targetsToBuild.Values,
                        new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, TaskScheduler = TargetTaskScheduler },
                        target =>
                        {
                            using (Profiler.BeginZone($"ResolveArgument | {target.Name}", color: (uint)Profiler.ColorType.Pink))
                            {
                                target.ResolveArguments();
                            }
                        });
                });
            }

            List<Target> sortedTargets;
            Log.Verbose("Sorting... ");
            using (Profiler.BeginZone("SortTargets", color: (uint)Profiler.ColorType.Purple))
            {
                sortedTargets = BuildTrace.Measure<List<Target>>("RunBuild.SortTargets", () => targetsToBuild.Values.OrderBy(target => target.Dependencies.Count).ToList());
            }

            Dictionary<PlanKey, TaskPlan> allTaskPlans;
            List<TaskPlan> executionOrder;

            Log.Verbose("Planning... ");
            using (Profiler.BeginZone("BuildEmitterPlans", color: (uint)Profiler.ColorType.Orange))
            {
                allTaskPlans = BuildTrace.Measure<Dictionary<PlanKey, TaskPlan>>("RunBuild.BuildEmitterPlans", () => BuildEmitterPlans(sortedTargets));
                BuildTrace.Mark("RunBuild.BuildEmitterPlans.count", allTaskPlans.Count.ToString());
            }

            using (Profiler.BeginZone("ResolvePlanDependencies", color: (uint)Profiler.ColorType.Cyan))
            {
                BuildTrace.Measure("RunBuild.ResolvePlanDependencies", () => ResolvePlanDependencies(allTaskPlans));
            }

            Log.Verbose("Topological Sorting... ");
            using (Profiler.BeginZone("SortTaskPlans", color: (uint)Profiler.ColorType.Magenta))
            {
                executionOrder = BuildTrace.Measure<List<TaskPlan>>("RunBuild.SortTaskPlans", () => TopologicalSort(allTaskPlans));
            }

            using (Profiler.BeginZone("ExecuteEmitterTasksWithSchedulers", color: (uint)Profiler.ColorType.Red))
            {
                BuildTrace.Measure("RunBuild.ExecuteEmitterTasksWithSchedulers", () => ExecuteEmitterTasksWithSchedulers(executionOrder));
            }

            BuildTrace.Measure("RunBuild.WaitAllManagedTasks", WaitAllManagedTasks);
        }

        private Dictionary<PlanKey, TaskPlan> BuildEmitterPlans(List<Target> sortedTargets)
        {
            var allTaskPlans = new Dictionary<PlanKey, TaskPlan>();

            foreach (var target in sortedTargets)
            {
                foreach (var emitterEntry in _taskEmitters)
                {
                    var emitterName = emitterEntry.Key;
                    var emitter = emitterEntry.Value;

                    if (!emitter.EnableEmitter(this, target))
                        continue;

                    var plan = new TaskPlan
                    {
                        Key = new PlanKey
                        {
                            Target = target.Name,
                            Emitter = emitterName
                        },
                        Target = target,
                        Emitter = emitter
                    };
                    allTaskPlans[plan.Key] = plan;
                }
            }

            return allTaskPlans;
        }

        private void ResolvePlanDependencies(Dictionary<PlanKey, TaskPlan> allTaskPlans)
        {
            foreach (var planEntry in allTaskPlans)
            {
                var plan = planEntry.Value;
                var target = plan.Target;
                var emitter = plan.Emitter;

                foreach (var dependencyTarget in target.Dependencies)
                {
                    foreach (var dependencyEmitter in emitter.Dependencies.Where(entry => entry.Value.Equals(DependencyModel.ExternalTarget)))
                    {
                        var dependencyKey = new PlanKey
                        {
                            Target = dependencyTarget,
                            Emitter = dependencyEmitter.Key
                        };

                        if (allTaskPlans.ContainsKey(dependencyKey))
                            plan.DirectDependencies.Add(dependencyKey);
                    }
                }

                foreach (var dependency in emitter.Dependencies.Where(entry => entry.Value.Equals(DependencyModel.PerTarget)))
                {
                    var dependencyKey = new PlanKey
                    {
                        Target = target.Name,
                        Emitter = dependency.Key
                    };

                    if (allTaskPlans.ContainsKey(dependencyKey))
                        plan.DirectDependencies.Add(dependencyKey);
                }

                foreach (var targetDependency in emitter.TargetDependencies)
                {
                    var dependencyKey = new PlanKey
                    {
                        Target = targetDependency.Key,
                        Emitter = targetDependency.Value
                    };

                    if (allTaskPlans.ContainsKey(dependencyKey))
                        plan.DirectDependencies.Add(dependencyKey);
                }
            }
        }

        private static List<TaskPlan> TopologicalSort(Dictionary<PlanKey, TaskPlan> allTaskPlans)
        {
            var executionOrder = new List<TaskPlan>(allTaskPlans.Count);
            var inDegree = new Dictionary<PlanKey, int>(allTaskPlans.Count);
            var reverseEdges = new Dictionary<PlanKey, List<PlanKey>>(allTaskPlans.Count);
            var queue = new Queue<TaskPlan>();

            foreach (var planEntry in allTaskPlans)
            {
                int degree = 0;
                foreach (var dep in planEntry.Value.DirectDependencies)
                {
                    if (!allTaskPlans.ContainsKey(dep))
                        continue;
                    degree++;
                    if (!reverseEdges.TryGetValue(dep, out var dependents))
                    {
                        dependents = new List<PlanKey>();
                        reverseEdges[dep] = dependents;
                    }
                    dependents.Add(planEntry.Key);
                }
                inDegree[planEntry.Key] = degree;
                if (degree == 0)
                    queue.Enqueue(planEntry.Value);
            }

            while (queue.Count > 0)
            {
                var plan = queue.Dequeue();
                executionOrder.Add(plan);

                if (!reverseEdges.TryGetValue(plan.Key, out var dependents))
                    continue;

                foreach (var dependentKey in dependents)
                {
                    if (--inDegree[dependentKey] == 0)
                        queue.Enqueue(allTaskPlans[dependentKey]);
                }
            }

            if (executionOrder.Count != allTaskPlans.Count)
                throw new TaskFatalError("Circular dependency detected in task plans!");

            return executionOrder;
        }

        private void ExecuteEmitterTasksWithSchedulers(List<TaskPlan> executionOrder)
        {
            using var trace = BuildTrace.Scope("RunBuild.ExecuteEmitterTasksWithSchedulers.detail");
            var runningPlans = new ConcurrentDictionary<PlanKey, Task>();
            var allTasks = new List<Task>();

            uint allTaskCount = 0;
            uint fileTaskCount = 0;
            foreach (var plan in executionOrder)
            {
                if (plan.Emitter.EmitTargetTask(this, plan.Target))
                    allTaskCount++;

                foreach (var fileList in plan.Target.FileLists.Where(fileList => plan.Emitter.EmitFileTask(this, plan.Target, fileList)))
                {
                    foreach (var _ in fileList.Files)
                    {
                        fileTaskCount++;
                        allTaskCount++;
                    }
                }
            }

            _allTaskCounter = 0;
            _fileTaskCounter = 0;
            BuildTrace.Mark("RunBuild.ExecuteEmitterTasksWithSchedulers.counts", $"plans={executionOrder.Count} all_tasks={allTaskCount} file_tasks={fileTaskCount}");

            foreach (var plan in executionOrder)
            {
                var taskExecution = Task.Run(async () =>
                {
                    foreach (var dependencyKey in plan.DirectDependencies)
                    {
                        if (runningPlans.TryGetValue(dependencyKey, out var dependencyTask))
                            await dependencyTask;
                    }

                    await ExecuteEmitterTaskWithSchedulers(plan, allTaskCount, fileTaskCount);
                });

                runningPlans[plan.Key] = taskExecution;
                allTasks.Add(taskExecution);
            }

            Task.WaitAll(allTasks.ToArray());
        }

        private async Task ExecuteEmitterTaskWithSchedulers(TaskPlan plan, uint allTaskCount, uint fileTaskCount)
        {
            var target = plan.Target;
            var emitter = plan.Emitter;

            List<Task> fileTasks = new();
            Task perTargetEmitterTask = Task.CompletedTask;

            if (emitter.EmitTargetTask(this, target))
            {
                perTargetEmitterTask = RunManagedTask(
                    new BuildTaskKey { TargetName = target.Name, File = "", TaskName = emitter.Name },
                    async () =>
                    {
                        var taskIndex = Interlocked.Increment(ref _allTaskCounter);
                        var percentage = allTaskCount == 0 ? 100.0f : 100.0f * taskIndex / allTaskCount;

                        using (Profiler.BeginZone($"{emitter.Name} | {target.Name}", color: (uint)Profiler.ColorType.Green1))
                        {
                            Stopwatch sw = new();
                            sw.Start();
                            var targetTaskArtifact = emitter.PerTargetTask(this, target);
                            sw.Stop();

                            if (targetTaskArtifact is not null)
                            {
                                _artifacts.Add(targetTaskArtifact);
                                if (!targetTaskArtifact.IsRestored)
                                {
                                    var costTime = sw.ElapsedMilliseconds;
                                    Log.Information("[{Percentage:00.0}%]: {EmitterName} {TargetName}, cost {CostTime:00.00}s",
                                        percentage, emitter.Name, target.Name, costTime / 1000.0f);
                                }
                            }
                        }
                        return await Task.FromResult(true);
                    }, TargetTaskScheduler);
            }

            await perTargetEmitterTask;

            foreach (var fileList in target.FileLists.ToArray().Where(fileList => emitter.EmitFileTask(this, target, fileList)))
            {
                var filesCopy = fileList.Files.ToArray();
                foreach (var file in filesCopy)
                {
                    var fileTask = RunManagedTask(
                        new BuildTaskKey { TargetName = target.Name, File = file, TaskName = emitter.Name },
                        async () =>
                        {
                            var fileTaskIndex = Interlocked.Increment(ref _fileTaskCounter);
                            var taskIndex = Interlocked.Increment(ref _allTaskCounter);
                            var percentage = allTaskCount == 0 ? 100.0f : 100.0f * taskIndex / allTaskCount;

                            using (Profiler.BeginZone($"{emitter.Name} | {target.Name} | {file}", color: (uint)Profiler.ColorType.Yellow1))
                            {
                                Stopwatch sw = new();
                                sw.Start();
                                var fileTaskArtifact = emitter.PerFileTask(this, target, fileList, fileList.GetFileOptions(file), file);
                                sw.Stop();

                                if (fileTaskArtifact is not null)
                                {
                                    _artifacts.Add(fileTaskArtifact);
                                    if (!fileTaskArtifact.IsRestored)
                                    {
                                        var costTime = sw.ElapsedMilliseconds;
                                        Log.Information("[{Percentage:00.0}%][{FileTaskIndex}/{FileTaskCount}]: {EmitterName} {TargetName}: {FileName}, cost {CostTime:00.00}s",
                                            percentage, fileTaskIndex, fileTaskCount, emitter.Name, target.Name, file, costTime / 1000.0f);
                                    }
                                }
                            }
                            return await Task.FromResult(true);
                        }, FileTaskScheduler);

                    fileTasks.Add(fileTask);
                }
            }

            await Task.WhenAll(fileTasks);
        }

        public void Cleanup()
        {
            _allTargets.Clear();
            _allPackages.Clear();
            _taskEmitters.Clear();
            while (_artifacts.TryTake(out _))
            {
            }
            _firstRun = true;
            _targetInitializationHooks = CreateDefaultTargetInitializationHooks();
            _useProfile = true;
            _emscriptenThreads = true;
            _setups.Clear();
            _setupOrder.Clear();
            _completedSetups.Clear();
            _runningTasks.Clear();
            while (_fatalErrors.TryDequeue(out _))
            {
            }
            _stopAll = false;
            _fatalErrorCount = 0;
        }
    }

    internal static class TargetTaskExtensions
    {
        public static void CallAllActions(this Target target, IList<Action<Target>> actions)
        {
            foreach (var action in actions)
            {
                using (Profiler.BeginZone($"Action | {target.Name}", color: (uint)Profiler.ColorType.Green))
                {
                    action(target);
                }
            }
        }
    }
}
