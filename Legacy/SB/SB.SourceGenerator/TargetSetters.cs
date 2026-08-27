using System;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis.Text;

namespace SB.Generators
{
    public static class TypeHelper
    {
        public static string GetFullTypeName(this ITypeSymbol Symbol) => Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        public static bool GetUnderlyingTypeIfIsArgumentList(this ITypeSymbol Symbol, out ITypeSymbol? ElementType)
        {
            ElementType = null;
            if (Symbol is INamedTypeSymbol)
                if (Symbol.MetadataName.Equals("ArgumentList`1"))
                {
                    ElementType = (Symbol as INamedTypeSymbol)!.TypeArguments[0];
                    return true;
                }
            return false;
        }
    }

    public struct MethodSignatureComparer : IEqualityComparer<IMethodSymbol>
    {
        public bool Equals(IMethodSymbol X, IMethodSymbol Y)
        {
            return X.Name == Y.Name;
        }

        public int GetHashCode(IMethodSymbol X)
        {
            return X.Name.GetHashCode();
        }
    }

    public struct ArgumentDriverMethodInfo
    {
        public ArgumentDriverMethodInfo() { }
        public bool InheritBehavior = false;
        public bool PathBehavior = false;
        public string InjectType = "global::SB.Target";
    }

    [Generator]
    public class TargetSetterGenerator : IIncrementalGenerator
    {
        public static ArgumentDriverMethodInfo GetArgumentDriverMethodInfo(IMethodSymbol Method, AttributeData TargetProperty, AttributeData? ArgumentDriverAttr)
        {
            ArgumentDriverMethodInfo Info = new();
            foreach (var Arg in TargetProperty.NamedArguments)
            {
                if (Arg.Key == "InheritBehavior" && Arg.Value.Value!.Equals(true))
                {
                    Info.InheritBehavior = true;
                    continue;
                }
                if (Arg.Key == "PathBehavior" && Arg.Value.Value!.Equals(true))
                {
                    Info.PathBehavior = true;
                    continue;
                }
            }
            if (ArgumentDriverAttr is not null)
            {
                foreach (var Arg in ArgumentDriverAttr.NamedArguments)
                {
                    if (Arg.Key == "InjectType" && !(Arg.Value.Value is null))
                    {
                        Info.InjectType = Arg.Value.Value.ToString();
                        continue;
                    }
                }
            }
            return Info;
        }

        public void Initialize(IncrementalGeneratorInitializationContext initContext)
        {
            IncrementalValueProvider<Compilation> AsyncCompile = initContext.CompilationProvider;
            GenerateArgumentSetters(initContext, AsyncCompile);
            GenerateTargetProxyMethods(initContext, AsyncCompile);
        }

        private static void RecursiveGetAllTypes(Compilation Compile, INamespaceSymbol Namespace, ref List<INamedTypeSymbol> Types, bool OnlyThisAsm = true)
        {
            foreach (var Type in Namespace.GetTypeMembers())
            {
                if (!OnlyThisAsm || SymbolEqualityComparer.Default.Equals(Type.ContainingAssembly, Compile.Assembly))
                    Types.Add(Type);
            }
            foreach (var NS in Namespace.GetNamespaceMembers())
            {
                RecursiveGetAllTypes(Compile, NS, ref Types, OnlyThisAsm);
            }
        }

        private void GenerateArgumentSetters(IncrementalGeneratorInitializationContext initContext, IncrementalValueProvider<Compilation> AsyncCompile)
        {
            var ArgumentDriverMethodFilter = AsyncCompile.Select((Compile, Cancel) =>
            {
                var Methods = new Dictionary<IMethodSymbol, ArgumentDriverMethodInfo>(SymbolEqualityComparer.Default);
                var Types = new List<INamedTypeSymbol>();
                RecursiveGetAllTypes(Compile, Compile.GlobalNamespace, ref Types, true);

                var ArgumentDrivers = Types.Where(T => T.Interfaces.Any(I => I.GetFullTypeName().Equals("global::SB.Core.IArgumentDriver")));
                // sort to avoid IDE bugs
                var SortedDrivers = ArgumentDrivers.ToList();
                SortedDrivers.Sort((A, B) => A.Name.CompareTo(B.Name));
                var ArgumentDriverMembers = SortedDrivers.SelectMany(T => T.GetMembers());
                var ArgumentDriverMethods = ArgumentDriverMembers.Where(M => M.Kind == SymbolKind.Method);
                // sort to avoid IDE bugs
                var SortedMethods = ArgumentDriverMethods.ToList();
                SortedMethods.Sort((A, B) => A.Name.CompareTo(B.Name));
                foreach (var Method in SortedMethods)
                {
                    var DriverType = Method.ContainingType;
                    AttributeData? DriverAttribute = null;
                    foreach (var A in DriverType.GetAttributes())
                        DriverAttribute = A.AttributeClass!.GetFullTypeName().Equals("global::SB.Core.ArgumentDriverAttribute") ? A : null;

                    AttributeData? TargetProperty = null;
                    foreach (var A in Method.GetAttributes())
                        TargetProperty = A.AttributeClass!.GetFullTypeName().Equals("global::SB.Core.TargetPropertyAttribute") ? A : null;

                    if (TargetProperty != null && !Methods.Any(KVP => KVP.Key.Name == Method.Name))
                    {
                        Methods.Add((IMethodSymbol)Method, GetArgumentDriverMethodInfo((IMethodSymbol)Method, TargetProperty, DriverAttribute));
                    }
                }
                return Methods;
            });

            // generate a class that contains their values as const strings
            initContext.RegisterSourceOutput(ArgumentDriverMethodFilter, (spc, Methods) =>
            {
                StringBuilder sourceBuilder = new StringBuilder(@"
using SB.Core;

namespace SB
{
    public static partial class ArgumentDictionaryExtensions
    {
");
                foreach (var MethodAndProperty in Methods)
                {
                    var Method = MethodAndProperty.Key;
                    var DriverMethodInfo = MethodAndProperty.Value;
                    var InheritBehavior = DriverMethodInfo.InheritBehavior;
                    var PathBehavior = DriverMethodInfo.PathBehavior;
                    var Param = Method.Parameters[0];
                    var MethodName = Method.Name;
                    {
                        var ArgumentsContainer = InheritBehavior ? "GetArgumentsContainer(Visibility)" : "FinalArguments";
                        var PropertyP = $"params {Param.Type.GetFullTypeName()} {Param.Name}";
                        var ParamP = PathBehavior ? $"@this.HandlePath({Param.Name})" : Param.Name;
                        if (Param.Type.GetUnderlyingTypeIfIsArgumentList(out var ElementType))
                        {
                            sourceBuilder.Append($@"
        public static void {MethodName}(this ArgumentDictionary @this, params {ElementType!.GetFullTypeName()}[] {Param.Name}) {{ @this.AppendToArgumentList<{ElementType!.GetFullTypeName()}>(""{MethodName}"", {ParamP}); }}
");
                        }
                        else
                        {
                            if (InheritBehavior)
                                throw new Exception($"{MethodName} fails: Single param setters should not have inherit behavior!");
                            sourceBuilder.Append($@"
        public static void {MethodName}(this ArgumentDictionary @this, {Param.Type.GetFullTypeName()} {Param.Name}) {{ @this.Override(""{MethodName}"", {ParamP}); }}"
);
                        }
                    }
                }

                sourceBuilder.Append(@"
    }

    public static partial class TargetSettersExtensions
    {
");

                foreach (var MethodAndProperty in Methods)
                {
                    var Method = MethodAndProperty.Key;
                    var DriverMethodInfo = MethodAndProperty.Value;
                    var InheritBehavior = DriverMethodInfo.InheritBehavior;
                    var InjectType = DriverMethodInfo.InjectType;
                    var Param = Method.Parameters[0];
                    var MethodName = Method.Name;
                    {
                        var FlagsP = InheritBehavior ? $"Visibility Visibility, " : "";
                        var ArgumentsContainer = InheritBehavior ? "GetArgumentsContainer(Visibility)" : "PrivateArguments";
                        var PropertyP = $"params {Param.Type.GetFullTypeName()} {Param.Name}";

                        if (Param.Type.GetUnderlyingTypeIfIsArgumentList(out var ElementType))
                        {
                            sourceBuilder.Append($@"
        public static {InjectType} {MethodName}(this {InjectType} @this, {FlagsP}params {ElementType!.GetFullTypeName()}[] {Param.Name}) {{ @this.{ArgumentsContainer}.{MethodName}({Param.Name}); return ({InjectType})@this; }}
");
                        }
                        else
                        {
                            if (InheritBehavior)
                                throw new Exception($"{MethodName} fails: Single param setters should not have inherit behavior!");
                            sourceBuilder.Append($@"
        public static {InjectType} {MethodName}(this {InjectType} @this, {FlagsP}{Param.Type.GetFullTypeName()} {Param.Name}) {{ @this.{ArgumentsContainer}.{MethodName}({Param.Name}); return ({InjectType})@this; }}"
);
                        }
                    }
                }

                sourceBuilder.Append(@"
    }
}");
                spc.AddSource("TargetSetters.cs", SourceText.From(sourceBuilder.ToString(), Encoding.UTF8));
            });
        }

        private struct TargetAPIProxyPayload
        {
            public TargetAPIProxyPayload() { }
            public INamedTypeSymbol? TargetType;
            public List<IMethodSymbol?> APIs = new();
            public List<IMethodSymbol?> Extensions = new();
            public List<INamedTypeSymbol> Derives = new();
        }
        
        private void GenerateTargetProxyMethods(IncrementalGeneratorInitializationContext initContext, IncrementalValueProvider<Compilation> AsyncCompile)
        {
            var TargetAPIFilter = AsyncCompile.Select((Compile, Cancel) =>
            {
                var Payload = new TargetAPIProxyPayload();
                var Types = new List<INamedTypeSymbol>();
                RecursiveGetAllTypes(Compile, Compile.GlobalNamespace, ref Types, false);

                Payload.TargetType = Compile.GetTypeByMetadataName("SB.Target");
                Payload.Derives = Types.Where(T => T.BaseType is not null)
                    .Where(T => SymbolEqualityComparer.Default.Equals(T.BaseType!, Payload.TargetType))
                    .ToList();

                bool NeedGenerateAPIProxy = SymbolEqualityComparer.Default.Equals(Payload.TargetType?.ContainingAssembly, Compile.Assembly);
                if (NeedGenerateAPIProxy && Payload.TargetType is not null)
                {
                    Payload.APIs = Payload.TargetType.GetMembers()
                        .Where(M => M.Kind == SymbolKind.Method)
                        .Select(M => M as IMethodSymbol)
                        .Where(M => SymbolEqualityComparer.Default.Equals(M!.ReturnType, Payload.TargetType))
                        .ToList();
                }

                if (Payload.TargetType is not null)
                {
                    Payload.Extensions = Types.Where(T => T.IsStatic)
                        .Where(T => SymbolEqualityComparer.Default.Equals(T.ContainingAssembly, Compile.Assembly))
                        .SelectMany(T => T.GetMembers())
                        .Where(M => M.IsStatic && M.Kind == SymbolKind.Method)
                        .Select(M => M as IMethodSymbol)
                        .Where(M => M!.IsExtensionMethod && M!.DeclaredAccessibility == Accessibility.Public)
                        .Where(M => SymbolEqualityComparer.Default.Equals(M!.Parameters[0].Type, Payload.TargetType))
                        .Where(M => SymbolEqualityComparer.Default.Equals(M!.ReturnType, Payload.TargetType))
                        .ToList();
                }

                return Payload;
            });

            initContext.RegisterSourceOutput(TargetAPIFilter, (spc, Payload) =>
            {
                StringBuilder sourceBuilder = new StringBuilder(@"
using SB.Core;

namespace SB
{
");
                // generate proxies for target APIs
                foreach (var DeriveType in Payload.Derives)
                {
                    if (Payload.APIs.Count == 0)
                        break;

                    sourceBuilder.Append(@$"
    public partial class {DeriveType.Name} : Target
    {{");
                    foreach (var API in Payload.APIs)
                    {
                        string ParameterString = "";
                        string ArgsString = "";
                        for (int i = 0; i < API!.Parameters.Length; i++)
                        {
                            var Param = API!.Parameters[i];
                            if (Param.IsParams)
                                ParameterString += "params ";
                            ParameterString += $"{Param.Type.GetFullTypeName()} {Param.Name}";
                            ArgsString += $"{Param.Name}";
                            if (i != (API!.Parameters.Length - 1))
                            {
                                ParameterString += ", ";
                                ArgsString += ", ";
                            }
                        }
                        sourceBuilder.Append($@"
        new public {DeriveType} {API!.Name}({ParameterString}) {{ base.{API!.Name}({ArgsString}); return this; }}");
                    }
                    sourceBuilder.Append(@"
    }");
                }
                
                // generate proxies for target extensions
                sourceBuilder.Append(@"
    public static partial class TargetExtensionProxy
    {");
                foreach (var Extension in Payload.Extensions)
                {
                    foreach (var DeriveType in Payload.Derives)
                    {
                        string ParameterString = "";
                        string ArgsString = "";
                        for (int i = 1; i < Extension!.Parameters.Length; i++)
                        {
                            ParameterString += ", ";
                            var Param = Extension!.Parameters[i];
                            if (Param.IsParams)
                                ParameterString += "params ";
                            ParameterString += $"{Param.Type.GetFullTypeName()} {Param.Name}";
                            ArgsString += $"{Param.Name}";
                            if (i != (Extension!.Parameters.Length - 1))
                            {
                                ArgsString += ", ";
                            }
                        }
                        sourceBuilder.Append($@"
        public static {DeriveType} {Extension!.Name}(this {DeriveType} @this{ParameterString}) {{ (@this as Target).{Extension!.Name}({ArgsString}); return @this; }}");
                    }
                }
                sourceBuilder.Append(@"
    }");
                sourceBuilder.Append(@"
}");
                spc.AddSource("TargetAPIProxy.cs", SourceText.From(sourceBuilder.ToString(), Encoding.UTF8));
            });
        }
    }
}