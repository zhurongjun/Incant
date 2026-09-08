using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Incant.Arguments.Generator;

/// <summary>Generates typed extensions from explicitly marked configuration keys.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class ArgumentExtensionsGenerator : IIncrementalGenerator
{
    private static readonly SymbolDisplayFormat s_typeFormat = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
        SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly DiagnosticDescriptor s_invalid = new DiagnosticDescriptor(
        "INCARG001", "Invalid argument declaration", "{0}", "Arguments", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor s_duplicate = new DiagnosticDescriptor(
        "INCARG002", "Conflicting argument extensions", "Generated method '{0}' is ambiguous",
        "Arguments", DiagnosticSeverity.Error, true);

    /// <summary>Registers attribute discovery with comparable, symbol-free intermediate values.</summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<Declaration> declarations = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Incant.Core.Arguments.GenerateArgumentAttribute",
            static (node, _) => node is VariableDeclaratorSyntax or PropertyDeclarationSyntax,
            static (attribute, _) => Describe(attribute));
        context.RegisterSourceOutput(declarations.Collect(), static (output, values) => Emit(output, values));
    }

    private static Declaration Describe(GeneratorAttributeSyntaxContext context)
    {
        ISymbol symbol = context.TargetSymbol;
        ITypeSymbol? type = symbol switch
        {
            IFieldSymbol field when field.IsStatic && field.IsReadOnly => field.Type,
            IPropertySymbol property when property.IsStatic && property.SetMethod is null => property.Type,
            _ => null,
        };
        string name = context.Attributes[0].ConstructorArguments.FirstOrDefault().Value as string ?? symbol.Name;
        string scope = symbol.ContainingNamespace.IsGlobalNamespace ? "" : symbol.ContainingNamespace.ToDisplayString();
        string owner = symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string error = "";
        string valueType = "";
        string elementType = "";
        bool isMap = false;
        if (type is not INamedTypeSymbol named || named.OriginalDefinition.ToDisplayString()
            != "Incant.Core.Arguments.ArgumentKey<T>" || string.IsNullOrWhiteSpace(name) || !SyntaxFacts.IsValidIdentifier("With" + name)
            || symbol.DeclaredAccessibility != Accessibility.Public
            || symbol.ContainingType.Arity != 0 || symbol.ContainingType.ContainingType is not null)
        {
            error = $"'{symbol.Name}' requires a public static readonly ArgumentKey<T> field or get-only property in a non-nested, non-generic type and a valid method suffix.";
        }
        else
        {
            ITypeSymbol value = named.TypeArguments[0];
            valueType = value.ToDisplayString(s_typeFormat);
            if (value is INamedTypeSymbol sequence && sequence.OriginalDefinition.ToDisplayString()
                == "System.Collections.Generic.IReadOnlyList<T>")
            {
                elementType = sequence.TypeArguments[0].ToDisplayString(s_typeFormat);
            }
            else if (value is INamedTypeSymbol map && map.OriginalDefinition.ToDisplayString()
                == "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
                && map.TypeArguments[0].SpecialType == SpecialType.System_String)
            {
                elementType = "string";
                isMap = true;
            }
        }

        string className = symbol.ContainingType.Name + "Extensions";
        if (symbol.ContainingNamespace.GetTypeMembers(className).Length > 0)
        {
            error = $"Generated type '{className}' conflicts with an existing declaration.";
        }

        return new Declaration(scope, owner, className, symbol.Name, name, valueType, elementType, isMap, error,
            symbol.ContainingType.DeclaredAccessibility == Accessibility.Public);
    }

    private static void Emit(SourceProductionContext context, ImmutableArray<Declaration> declarations)
    {
        foreach (Declaration declaration in declarations.Where(item => item.Error.Length > 0))
        {
            context.ReportDiagnostic(Diagnostic.Create(s_invalid, Location.None, declaration.Error));
        }

        Declaration[] valid = declarations.Where(item => item.Error.Length == 0).ToArray();
        var rejected = new HashSet<Declaration>();
        foreach (IGrouping<string, Declaration> group in valid.GroupBy(item => item.Scope + "." + item.Name))
        {
            if (group.Count() > 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(s_duplicate, Location.None, group.Key));
                rejected.UnionWith(group);
            }
        }

        foreach (IGrouping<string, Declaration> group in valid.Where(item => !rejected.Contains(item)).GroupBy(item => item.Owner))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            Declaration first = group.First();
            var source = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
            if (first.Scope.Length > 0)
            {
                source.Append("namespace ").Append(first.Scope).Append(";\n");
            }

            source.Append(first.IsPublic ? "public" : "internal").Append(" static class ")
                .Append(first.ClassName).Append("\n{\n");
            foreach (Declaration item in group.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                const string Set = "global::Incant.Core.Arguments.ArgumentSet";
                const string Receiver = "(arguments ?? throw new global::System.ArgumentNullException(nameof(arguments)))";
                string key = item.Owner + ".@" + item.Member;
                source.Append("    /// <summary>Replaces a configuration field.</summary>\n    public static ")
                    .Append(Set).Append(" With").Append(item.Name).Append("(this ").Append(Set)
                    .Append(" arguments, ").Append(item.ValueType).Append(" value) => ").Append(Receiver).Append(".With(")
                    .Append(key).Append(", value);\n");
                source.Append("    /// <summary>Removes a configuration field.</summary>\n    public static ")
                    .Append(Set).Append(" Without").Append(item.Name).Append("(this ").Append(Set)
                    .Append(" arguments) => ").Append(Receiver).Append(".Remove(").Append(key).Append(");\n");
                if (item.ElementType.Length > 0)
                {
                    source.Append("    /// <summary>Appends an ordered contribution.</summary>\n    public static ")
                        .Append(Set).Append(" Append").Append(item.Name).Append("(this ").Append(Set)
                        .Append(" arguments, ").Append(item.IsMap ? item.ValueType : "params " + item.ElementType + "[]")
                        .Append(" values) => ").Append(Receiver).Append(".Append(")
                        .Append(key).Append(", values);\n");
                    source.Append("    /// <summary>Removes matching elements.</summary>\n    public static ")
                        .Append(Set).Append(" Remove").Append(item.Name).Append("(this ").Append(Set)
                        .Append(" arguments, global::System.Predicate<").Append(item.ElementType)
                        .Append("> predicate) => ").Append(Receiver).Append(".RemoveItems(").Append(key).Append(", predicate);\n");
                }
            }

            source.Append("}\n");
            context.AddSource(first.Scope + "." + first.ClassName + ".g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
        }
    }

    private sealed class Declaration : IEquatable<Declaration>
    {
        internal Declaration(string scope, string owner, string className, string member, string name,
            string valueType, string elementType, bool isMap, string error, bool isPublic)
        {
            Scope = scope;
            Owner = owner;
            ClassName = className;
            Member = member;
            Name = name;
            ValueType = valueType;
            ElementType = elementType;
            IsMap = isMap;
            Error = error;
            IsPublic = isPublic;
        }

        internal string Scope { get; }

        internal string Owner { get; }

        internal string ClassName { get; }

        internal string Member { get; }

        internal string Name { get; }

        internal string ValueType { get; }

        internal string ElementType { get; }

        internal bool IsMap { get; }

        internal string Error { get; }

        internal bool IsPublic { get; }

        public bool Equals(Declaration? other) => other is not null && Scope == other.Scope && Owner == other.Owner
            && ClassName == other.ClassName && Member == other.Member && Name == other.Name
            && ValueType == other.ValueType && ElementType == other.ElementType && Error == other.Error
            && IsMap == other.IsMap && IsPublic == other.IsPublic;

        public override bool Equals(object? obj) => obj is Declaration other && Equals(other);

        public override int GetHashCode() => Owner.GetHashCode() ^ Member.GetHashCode() ^ ValueType.GetHashCode();
    }
}
