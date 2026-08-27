using SB;
using System.Runtime.CompilerServices;

namespace SB.Core
{
    public record PackageConfig
    {
        public required Version Version { get; init; }
    }

    public class Package
    {
        public Package(BuildInstance instance, string name)
        {
            Instance = instance;
            Name = name;
        }

        public Package AvailableVersions(params Version[] versions)
        {
            availableVersions.AddRange(versions);
            return this;
        }

        public Package AddTarget<T>(string targetName, Action<T, PackageConfig> installer, [CallerFilePath] string? location = null, [CallerLineNumber] int lineNumber = 0)
            where T : Target
        {
            if (Installers.TryGetValue(targetName, out var _))
                throw new PackageInstallException(Name, targetName, $"Package {Name}: Installer for target {targetName} already exists!");

            Action<Target, PackageConfig> installerWrapper = (Target target, PackageConfig config) =>
            {
                var typedTarget = target as T;
                installer(typedTarget!, config);
            };
            Installers.Add(targetName, new TargetInstaller {
                Loc = location!,
                LineNumber = lineNumber,
                Action = installerWrapper,
                TargetType = typeof(T)
            });
            return this;
        }

        internal Target AcquireTarget(string targetName, PackageConfig config)
        {
            if (availableVersions.Count > 0 && !availableVersions.Contains(config.Version))
                throw new PackageInstallException(Name, targetName, $"Package {Name}: Version {config.Version} not available!");

            Dictionary<PackageConfig, Target>? targetPermutations;
            if (!AcquiredTargets.TryGetValue(targetName, out targetPermutations))
            {
                targetPermutations = new();
                AcquiredTargets.Add(targetName, targetPermutations);
            }

            if (targetPermutations.TryGetValue(config, out var permutation))
            {
                return permutation;
            }
            else
            {
                TargetInstaller installer;
                if (!Installers.TryGetValue(targetName, out installer))
                    throw new PackageInstallException(Name, targetName, $"Package {Name}: Installer for target {targetName} not found!");

                var constructArgs = new object[] { Instance, $"{Name}@{targetName}@{config.Version}", true, installer.Loc, installer.LineNumber };
                Target? targetToInstall = Activator.CreateInstance(installer.TargetType, constructArgs) as Target;
                installer.Action(targetToInstall!, config);
                targetPermutations.Add(config, targetToInstall!);
                return targetToInstall!;
            }
        }

        internal struct TargetInstaller
        {
            public required string Loc;
            public required int LineNumber;
            public Action<Target, PackageConfig> Action;
            public Type TargetType;
        }

        public BuildInstance Instance { get; }
        public string Name { get; private set; }
        public IEnumerable<PackageTargetInfo> Targets => Installers
            .Select(entry => new PackageTargetInfo(
                entry.Key,
                entry.Value.TargetType,
                entry.Value.Loc,
                entry.Value.LineNumber));
        public IReadOnlyCollection<Version> Versions => availableVersions;
        internal HashSet<Version> availableVersions = new();
        internal Dictionary<string, TargetInstaller> Installers = new();
        internal Dictionary<string, Dictionary<PackageConfig, Target>> AcquiredTargets = new();
    }

    public sealed record PackageTargetInfo(
        string Name,
        Type TargetType,
        string Location,
        int LineNumber);

    public class PackageInstallException : Exception
    {
        public PackageInstallException(string PackageName, string TargetName, string? message) 
            : base(message)
        {
        
        }
    }
}
