using SB.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SB
{
    public sealed class TargetTagAttribute
    {
        private readonly SortedSet<string> _tags = new(StringComparer.Ordinal);

        public IReadOnlySet<string> Tags => _tags;

        public void Add(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                throw new ArgumentException("Target tag cannot be empty.");

            _tags.Add(tag.Trim());
        }

        public void AddRange(IEnumerable<string> tags)
        {
            foreach (var tag in tags)
                Add(tag);
        }
    }

    public sealed class PositiveBuildAttribute
    {
        public bool Enable { get; init; } = true;
    }

    public partial class Target : TargetSetters
    {
        public Target(BuildInstance instance, string name, bool isFromPackage, [CallerFilePath] string? location = null, [CallerLineNumber] int lineNumber = 0)
            : base(location!)
        {
            Instance = instance;
            var configures = instance.GetStage<Stages.LoadConfigures>()!;
            Name = name;
            Location = location!;
            LineNumber = lineNumber;
            IsFromPackage = isFromPackage;
            SetAttribute(new PositiveBuildAttribute { Enable = !isFromPackage });

            instance.TargetInitializationHooks(this);
            if (configures.Configurations.TryGetValue(configures.ConfigurationName, out var globalConfig))
                globalConfig(this);
            else
                throw new TaskFatalError($"Target {name}: Global configuration {configures.ConfigurationName} does not exist!");
        }

        public T FileList<T>()
            where T : FileList, new()
        {
            foreach (var existingFileList in FileLists)
            {
                if (existingFileList is T)
                    return (existingFileList as T)!;
            }
            var fileList = new T { Target = this };
            FileLists.Add(fileList);
            return fileList;
        }

        public bool HasFilesOf<T>() => FileLists.Exists(fileList => fileList is T && fileList.Files.Count > 0);

        public bool HasFileListOf<T>() => FileLists.Exists(fileList => fileList is T);

        public Target DependsOn(Visibility visibility, params string[] dependencyNames)
        {
            foreach (var dependencyName in dependencyNames)
            {
                switch (visibility)
                {
                    case Visibility.Public:
                        {
                            if (dependencyName.Contains("@"))
                                PublicPackageTargetDependencies.Add(dependencyName);
                            else
                                PublicTargetDependencies.Add(dependencyName);
                        }
                        break;
                    case Visibility.Private:
                        {
                            if (dependencyName.Contains("@"))
                                PrivatePackageTargetDependencies.Add(dependencyName);
                            else
                                PrivateTargetDependencies.Add(dependencyName);
                        }
                        break;
                    case Visibility.Interface:
                        {
                            if (dependencyName.Contains("@"))
                                InterfacePackageTargetDependencies.Add(dependencyName);
                            else
                                InterfaceTargetDependencies.Add(dependencyName);
                        }
                        break;
                }
            }
            return this;
        }

        public Target Require(string package, PackageConfig config)
        {
            if (PackageDependencies.TryGetValue(package, out var _))
                throw new ArgumentException($"Target {Name}: Required package {package} is already required!");
            PackageDependencies.Add(package, config);
            return this;
        }

        public Target BeforeBuild(Action<Target> action)
        {
            BeforeBuildActions.Add(action);
            return this;
        }

        internal bool HasBeforeBuildActions() => BeforeBuildActions.Count > 0;

        internal void CallBeforeBuildActions() => this.CallAllActions(BeforeBuildActions);

        public Target AfterLoad(Action<Target> action)
        {
            AfterLoadActions.Add(action);
            return this;
        }

        public void SetAttribute<T>(T attribute, bool overrideIfExisted = false)
        {
            if (overrideIfExisted && Attributes.TryGetValue(typeof(T), out var _))
            {
                Attributes.Remove(typeof(T));
            }
            Attributes.Add(typeof(T), attribute);
        }

        public T? GetAttribute<T>()
        {
            if (Attributes.TryGetValue(typeof(T), out var attribute))
                return (T?)attribute;
            return default;
        }
        public object? GetAttribute(string typeName)
        {
            foreach (var attribute in Attributes)
            {
                if (attribute.Key.Name == typeName)
                    return attribute.Value;
            }
            return default;
        }
        public bool HasAttribute<T>() => GetAttribute<T>() != null;

        private bool PackagesResolved = false;
        internal void ResolvePackages(ref Dictionary<string, Target> outPackageTargets)
        {
            if (!PackagesResolved)
            {
                foreach (var packageEntry in PackageDependencies)
                {
                    var packageName = packageEntry.Key;
                    var packageConfig = packageEntry.Value;
                    var package = Instance.GetPackage(packageName);
                    if (package == null)
                        throw new ArgumentException($"Target {Name}: Required package {packageName} does not exist!");

                    var resolvePackageTargetDependencies =
                        (SortedSet<string> packageTargetDependencies, ref SortedSet<string> resolvedTargetDependencies, ref Dictionary<string, Target> resolvedPackageTargets) =>
                        {
                            foreach (var nickName in packageTargetDependencies)
                            {
                                var splitted = nickName.Split("@");
                                if (splitted[0] == packageName)
                                {
                                    var packageTarget = package.AcquireTarget(splitted[1], packageConfig);
                                    {
                                        packageTarget.ResolvePackages(ref resolvedPackageTargets);
                                        resolvedPackageTargets.TryAdd(packageTarget.Name, packageTarget);
                                    }
                                    resolvedTargetDependencies.Add(packageTarget.Name);
                                }
                            }
                        };

                    resolvePackageTargetDependencies(PrivatePackageTargetDependencies, ref PrivateTargetDependencies, ref outPackageTargets);
                    resolvePackageTargetDependencies(PublicPackageTargetDependencies, ref PublicTargetDependencies, ref outPackageTargets);
                    resolvePackageTargetDependencies(InterfacePackageTargetDependencies, ref InterfaceTargetDependencies, ref outPackageTargets);
                }
                PackagesResolved = true;
            }
        }

        internal void ResolveDependencies()
        {
            if (bDependenciesResolved)
                return;

            RecursiveMergeDependencies(FinalTargetDependencies, PublicTargetDependencies);
            RecursiveMergeDependencies(FinalTargetDependencies, PrivateTargetDependencies);

            ResolveDependenciesIgnoreVisibility();
            bDependenciesResolved = true;
        }

        internal void RecursiveMergeDependencies(ISet<string> To, IReadOnlySet<string> DepNames)
        {
            To.AddRange(DepNames);
            foreach (var DepName in DepNames)
            {
                Target? DepTarget = Instance.GetTarget(DepName)!;
                if (DepTarget == null)
                    throw new TaskFatalError($"Target {Name}: Dependency {DepName} does not exist!");
                RecursiveMergeDependencies(To, DepTarget.PublicTargetDependencies);
                RecursiveMergeDependencies(To, DepTarget.InterfaceTargetDependencies);
            }
        }

        internal void ResolveDependenciesIgnoreVisibility()
        {
            RecursiveMergeDependenciesIgnoreVisibility(ignoreVisibilityAllDependencies, PublicTargetDependencies);
            RecursiveMergeDependenciesIgnoreVisibility(ignoreVisibilityAllDependencies, InterfaceDependencies);
            RecursiveMergeDependenciesIgnoreVisibility(ignoreVisibilityAllDependencies, PrivateTargetDependencies);
        }

        internal void RecursiveMergeDependenciesIgnoreVisibility(ISet<string> To, IReadOnlySet<string> DepNames)
        {
            To.AddRange(DepNames);
            foreach (var DepName in DepNames)
            {
                Target? DepTarget = Instance.GetTarget(DepName)!;
                if (DepTarget == null)
                    throw new TaskFatalError($"Target {Name}: Dependency {DepName} does not exist!");
                RecursiveMergeDependenciesIgnoreVisibility(To, DepTarget.PublicTargetDependencies);
                RecursiveMergeDependenciesIgnoreVisibility(To, DepTarget.InterfaceTargetDependencies);
                RecursiveMergeDependenciesIgnoreVisibility(To, DepTarget.PrivateTargetDependencies);
            }
        }

        internal void ResolveArguments()
        {
            if (bArgumentsResolved)
                return;

            // Files
            foreach (var FileList in FileLists)
            {
                using (Profiler.BeginZone($"GlobFiles | {Name}", color: (uint)Profiler.ColorType.Pink))
                {
                    FileList.GlobFiles();
                }
            }
            // Arguments
            FinalArguments.Merge(PublicArguments, false);
            FinalArguments.Merge(PrivateArguments, false);
            foreach (var DepName in Dependencies)
            {
                Target DepTarget = Instance.GetTarget(DepName)!;
                if (DepTarget.IsBinaryDependencyTarget())
                    continue;

                FinalArguments.Merge(DepTarget.PublicArguments, false);
                FinalArguments.Merge(DepTarget.InterfaceArguments, false);
            }
            bArgumentsResolved = true;
        }
        private bool bDependenciesResolved = false;
        private bool bArgumentsResolved = false;

        private Dictionary<Type, object?> Attributes = new();
        public IReadOnlySet<string> IgnoreVisibilityAllDependencies => ignoreVisibilityAllDependencies;
        public IReadOnlySet<string> Dependencies => FinalTargetDependencies;
        public IReadOnlySet<string> PublicDependencies => PublicTargetDependencies;
        public IReadOnlySet<string> PrivateDependencies => PrivateTargetDependencies;
        public IReadOnlySet<string> InterfaceDependencies => InterfaceTargetDependencies;

        public BuildInstance Instance { get; }
        public string Name { get; }
        public string Location { get; }
        public int LineNumber { get; }

        #region Files
        internal List<FileList> FileLists = new();
        #endregion

        #region Dependencies
        private SortedDictionary<string, PackageConfig> PackageDependencies = new();

        private SortedSet<string> ignoreVisibilityAllDependencies = new();
        private SortedSet<string> FinalTargetDependencies = new();
        private SortedSet<string> PublicTargetDependencies = new();
        private SortedSet<string> PrivateTargetDependencies = new();
        private SortedSet<string> InterfaceTargetDependencies = new();

        private SortedSet<string> PublicPackageTargetDependencies = new();
        private SortedSet<string> PrivatePackageTargetDependencies = new();
        private SortedSet<string> InterfacePackageTargetDependencies = new();
        #endregion

        #region Package
        public bool IsFromPackage { get; } = false;
        #endregion

        internal List<Action<Target>> BeforeBuildActions = new();
        internal List<Action<Target>> AfterLoadActions = new();
    }

    public static partial class TargetExtensions
    {
        public static ArgumentDictionary Copy(this ArgumentDictionary @this)
        {
            var copiedArguments = new ArgumentDictionary();
            copiedArguments.Merge(@this, true);
            return copiedArguments;
        }

        public static void Merge(this ArgumentDictionary to, ArgumentDictionary? from, bool allowOverride)
        {
            if (from is null)
                return;
                
            foreach (var entry in from)
            {
                var argumentName = entry.Key;
                var argumentValue = entry.Value;
                if (argumentValue is IArgumentList)
                {
                    var argumentList = argumentValue as IArgumentList;
                    if (to.TryGetValue(argumentName, out var existingValue))
                    {
                        var copiedArgumentList = (existingValue as IArgumentList)!.Copy();
                        copiedArgumentList.Merge(argumentList!);
                        to[argumentName] = copiedArgumentList;
                    }
                    else
                        to.Add(argumentName, argumentList!.Copy());
                }
                else
                {
                    if (!to.TryGetValue(argumentName, out var existingValue))
                    {
                        to.Add(argumentName, argumentValue);
                    }
                    else if (allowOverride)
                    {
                        to[argumentName] = argumentValue;
                    }
                    else
                    {
                        throw new TaskFatalError("Argument Confict!");
                    }
                }
            }
        }

        public static TargetType? GetTargetType(this Target target)
        {
            if (target.PrivateArguments.TryGetValue("TargetType", out var targetType))
                return (TargetType?)targetType;
            return null;
        }

        public static bool IsBinaryDependencyTarget(this Target target)
        {
            return target.GetTargetType() == TargetType.Executable;
        }

        public static T AddTag<T>(this T @this, string tag)
            where T : Target
        {
            var attribute = @this.GetAttribute<TargetTagAttribute>();
            if (attribute is null)
            {
                attribute = new TargetTagAttribute();
                @this.SetAttribute(attribute);
            }
            attribute.Add(tag);
            return @this;
        }

        public static T AddTags<T>(this T @this, params string[] tags)
            where T : Target
        {
            return @this.AddTags((IEnumerable<string>)tags);
        }

        public static T AddTags<T>(this T @this, IEnumerable<string> tags)
            where T : Target
        {
            var attribute = @this.GetAttribute<TargetTagAttribute>();
            if (attribute is null)
            {
                attribute = new TargetTagAttribute();
                @this.SetAttribute(attribute);
            }
            attribute.AddRange(tags);
            return @this;
        }

        public static bool HasTag(this Target @this, string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return false;

            return @this.GetAttribute<TargetTagAttribute>()?.Tags.Contains(tag.Trim()) ?? false;
        }

        public static bool HasAnyTag(this Target @this, params string[] tags)
        {
            return @this.HasAnyTag((IEnumerable<string>)tags);
        }

        public static bool HasAnyTag(this Target @this, IEnumerable<string> tags)
        {
            return tags.Any(@this.HasTag);
        }

        public static bool HasAllTags(this Target @this, params string[] tags)
        {
            return @this.HasAllTags((IEnumerable<string>)tags);
        }

        public static bool HasAllTags(this Target @this, IEnumerable<string> tags)
        {
            return tags.All(@this.HasTag);
        }

        public static IReadOnlySet<string> Tags(this Target @this)
        {
            return @this.GetAttribute<TargetTagAttribute>()?.Tags ?? new SortedSet<string>(StringComparer.Ordinal);
        }

        public static T SetPositiveBuild<T>(this T @this, bool enable)
            where T : Target
        {
            @this.SetAttribute(new PositiveBuildAttribute { Enable = enable }, true);
            return @this;
        }

        public static bool IsPositiveBuild(this Target @this)
        {
            return @this.GetAttribute<PositiveBuildAttribute>()?.Enable ?? true;
        }
    }
}
