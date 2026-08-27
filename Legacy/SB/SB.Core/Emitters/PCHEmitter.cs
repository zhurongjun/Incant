using SB.Core;
using Microsoft.Extensions.FileSystemGlobbing;

namespace SB
{
    using BS = BuildInstance;

    public class PCHEmitter : TaskEmitter
    {
        private static readonly string[] SharedPCHCompatibilityArguments =
        [
            "CppVersion",
            "Exception",
            "RTTI",
            "RuntimeLibrary",
            "DynamicDebug"
        ];

        public PCHEmitter(IToolchain toolchain) => this.Toolchain = toolchain;
        public override bool EnableEmitter(BuildInstance instance, Target target) => target.HasAttribute<CreateSharedPCHAttribute>() || target.HasAttribute<CreatePrivatePCHAttribute>();
        public override bool EmitTargetTask(BuildInstance instance, Target target) => true;
        public override IArtifact? PerTargetTask(BuildInstance instance, Target target)
        {
            var buildDatabase = target.Instance.GetStage<Stages.PrepareBuildDatabasesStage>()!;
            var createSharedPch = target.GetAttribute<CreateSharedPCHAttribute>();
            var createPrivatePch = target.GetAttribute<CreatePrivatePCHAttribute>();
            bool changed = false;

            var createPch = (CreatePCHAttribute createPchAttribute, PCHMode mode) => {
                var pchFile = GetPCHFile(target, mode);
                var pchAstFile = GetPCHASTFile(target, mode);

                Profiler.Message(mode.ToString());

                if (createPchAttribute.Globs.Count != 0)
                {
                    var globMatcher = new Matcher();
                    globMatcher.AddIncludePatterns(createPchAttribute.Globs);
                    createPchAttribute.Headers.AddRange(globMatcher.GetResultsInFullPath(target.Directory));
                }

                changed |= buildDatabase.GetCompileDatabaseForTarget(target).RunIfOutdated(target.Name, pchFile, "PCHEmitter.CreatePCH", (DependencyRecord depend) =>
                {
                    var pchIncludes = String.Join("\n", createPchAttribute.Headers.Select(header => $"#include \"{header}\""));
                    var pchFileContent = $"""
                    #pragma once
                    #ifdef __cplusplus
                    #ifdef _WIN32
                    #ifndef _CRT_SECURE_NO_WARNINGS
                    #define _CRT_SECURE_NO_WARNINGS 1
                    #endif
                    #include <intrin.h>
                    #endif
                    {pchIncludes}
                    #endif // __cplusplus
                    """;
                    File.WriteAllText(pchFile, pchFileContent);
                    depend.ExternalFiles.Add(pchFile);
                }, createPchAttribute.Headers, null);

                var sourceDependencies = Path.Combine(target.GetBuildSrcDepsDir(), BuildInstance.GetUniqueTempFileName(pchFile, target.Name + this.Name, "source.deps.json"));
                var pchArguments = target.Arguments;
                if (mode == PCHMode.Shared)
                {
                    var sharedPchArgs = (pchArguments as ArgumentDictionary)!.Copy();
                    // Remove all private defines & add all interface args
                    if (target.Arguments.TryGetValue("Defines", out var defs))
                    {
                        ArgumentList<string> defines = (defs as ArgumentList<string>)?.Copy() as ArgumentList<string> ?? new();
                        if (target.PrivateArguments.TryGetValue("Defines", out var privateDefines))
                        {
                            defines.Substract(privateDefines as ArgumentList<string>);
                        }
                        sharedPchArgs["Defines"] = defines;
                    }
                    sharedPchArgs.Merge(target.InterfaceArguments, false);
                    pchArguments = sharedPchArgs;
                }
                // Execute
                // TODO: SEPERATE C AND CPP PCH
                var compilerDriver = Toolchain.Compiler.CreateArgumentDriver(target.Instance, CFamily.Cpp, true)
                    .AddArguments(pchArguments)
                    .AddArgument("Source", pchFile)
                    .AddArgument("Object", pchAstFile)
                    .AddArgument("SourceDependencies", sourceDependencies);
                Toolchain.Compiler.Compile(this, target, compilerDriver, Path.GetDirectoryName(pchAstFile));
            };

            if (createSharedPch is not null)
                createPch(createSharedPch, PCHMode.Shared);
            if (createPrivatePch is not null)
                createPch(createPrivatePch, PCHMode.Private);

            return new PlainArtifact { IsRestored = !changed };
        }
        // 2025/3/24
        // under clang-cl, these two files must locate in the same directory
        // because the compiler has bugs for windows path
        internal static string GetPCHFile(Target target, PCHMode mode) => Path.Combine(target.GetBuildGenDir(), $"{mode}PCH.h");
        internal static string GetPCHASTFile(Target target, PCHMode mode) => Path.Combine(target.GetBuildGenDir(), $"{mode}PCH.pch");
        internal static bool ProvideSharedPCH(Target target) => target.GetAttribute<CreateSharedPCHAttribute>() is not null;
        internal static bool CanUseSharedPCH(Target consumer, Target provider)
        {
            foreach (var argumentName in SharedPCHCompatibilityArguments)
            {
                consumer.PrivateArguments.TryGetValue(argumentName, out var consumerValue);
                provider.PrivateArguments.TryGetValue(argumentName, out var providerValue);
                if (!Equals(consumerValue, providerValue))
                    return false;
            }
            return true;
        }
        private IToolchain Toolchain;
    }

    public enum PCHMode
    {
        Shared,
        Private
    }

    public class UsePCHAttribute
    {
        public PCHMode Mode { get; internal set; }
        public string? WantedSharedPCH { get; internal set; }
        public string? CppPCHAST { get; internal set; }
    }

    public abstract class CreatePCHAttribute
    {
        public List<string> Headers { get; init; } = new();
        public List<string> Globs { get; init; } = new();
    }

    public class CreateSharedPCHAttribute : CreatePCHAttribute {}
    
    public class CreatePrivatePCHAttribute : CreatePCHAttribute {}

    public static partial class TargetExtensions
    {
        public static CppTarget UseSharedPCH(this CppTarget @this, string? wanted = null)
        {
            var useAttribute = @this.GetAttribute<UsePCHAttribute>();
            if (useAttribute is null)
            {
                @this.SetAttribute(@this.SetupUsePCHAttribute(PCHMode.Shared));
            }
            else
            {
                useAttribute.Mode = PCHMode.Shared;
                useAttribute.WantedSharedPCH = wanted;
            }
            return @this;
        }

        public static CppTarget UsePrivatePCH(this CppTarget @this, params string[] headers)
        {
            var useAttribute = @this.GetAttribute<UsePCHAttribute>();
            if (useAttribute is null)
            {
                @this.SetAttribute(@this.SetupUsePCHAttribute(PCHMode.Private));
            }
            else
            {
                useAttribute.Mode = PCHMode.Private;
            }
                
            var createAttribute = new CreatePrivatePCHAttribute();
            @this.SetAttribute(createAttribute);
            foreach (var file in headers)
            {
                if (file.Contains("*"))
                {
                    if (Path.IsPathFullyQualified(file))
                        createAttribute.Globs.Add(Path.GetRelativePath(@this.Directory, file));
                    else
                        createAttribute.Globs.Add(file);
                }
                else if (Path.IsPathFullyQualified(file))
                    createAttribute.Headers.Add(file);
                else
                    createAttribute.Headers.Add(Path.Combine(@this.Directory, file));
            }
            return @this;
        }

        public static CppTarget CreateSharedPCH(this CppTarget @this, params string[] headers)
        {
            var createAttribute = new CreateSharedPCHAttribute();
            @this.SetAttribute(createAttribute);
            foreach (var file in headers)
            {
                if (file.Contains("*"))
                {
                    if (Path.IsPathFullyQualified(file))
                        createAttribute.Globs.Add(Path.GetRelativePath(@this.Directory, file));
                    else
                        createAttribute.Globs.Add(file);
                }
                else if (Path.IsPathFullyQualified(file))
                    createAttribute.Headers.Add(file);
                else
                    createAttribute.Headers.Add(Path.Combine(@this.Directory, file));
            }
            return @this;
        }

        private static UsePCHAttribute SetupUsePCHAttribute(this Target @this, PCHMode mode)
        {
            var useAttribute = new UsePCHAttribute { Mode = mode };
            @this.AfterLoad(target =>
            {
                string? pchProvider = null;
                if (useAttribute.Mode == PCHMode.Shared && target.Dependencies.Count > 0)
                {
                    if (useAttribute.WantedSharedPCH is null)
                    {
                        var providers = target.Dependencies
                            .Select(dependency => target.Instance.GetTarget(dependency)!)
                            .Where(dependencyTarget => PCHEmitter.ProvideSharedPCH(dependencyTarget))
                            .Where(dependencyTarget => PCHEmitter.CanUseSharedPCH(target, dependencyTarget));
                        if (providers.Any())
                        {
                            var scores = providers.Select(provider => 1 + provider.Dependencies.Count(providerDependency => PCHEmitter.ProvideSharedPCH(target.Instance.GetTarget(providerDependency)!)));
                            var bestProvider = providers.ElementAt(scores.ToList().IndexOf(scores.Max()));
                            pchProvider = bestProvider.Name;
                        }
                    }
                    else
                    {
                        var wantedProvider = target.Instance.GetTarget(useAttribute.WantedSharedPCH!);
                        if (wantedProvider is not null &&
                            PCHEmitter.ProvideSharedPCH(wantedProvider) &&
                            PCHEmitter.CanUseSharedPCH(target, wantedProvider))
                        {
                            pchProvider = wantedProvider.Name;
                        }
                    }
                }
                else if (useAttribute.Mode == PCHMode.Private)
                {
                    pchProvider = target.Name;
                }
                
                if (pchProvider is not null)
                {
                    // later we get it in cpp compile emitter
                    useAttribute.CppPCHAST = PCHEmitter.GetPCHASTFile(target.Instance.GetTarget(pchProvider)!, useAttribute.Mode);
                }
            });
            return useAttribute;
        }
    }
}
