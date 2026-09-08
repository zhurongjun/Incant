using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Incant.CX.Arguments.Generator;

/// <summary>Implements the fixed CX ArgumentSet properties and their typed transformations.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class ArgumentSetGenerator : IIncrementalGenerator
{
    private const string Owner = "Incant.CX.Arguments.ArgumentSet";
    private static readonly SymbolDisplayFormat s_typeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly DiagnosticDescriptor s_invalid = new DiagnosticDescriptor(
        "INCARG001", "Invalid CX argument property", "{0}", "Arguments", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor s_conflict = new DiagnosticDescriptor(
        "INCARG002", "Conflicting CX argument members", "Generated member '{0}' conflicts with another declaration",
        "Arguments", DiagnosticSeverity.Error, true);

    /// <summary>Discovers optional properties on the CX configuration type only.</summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<Declaration> fields = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Incant.CX.Arguments.ArgumentAttribute",
            static (node, _) => node is PropertyDeclarationSyntax,
            static (attribute, _) => Describe(attribute));
        context.RegisterSourceOutput(fields.Collect(), static (output, values) => Emit(output, values));
    }

    private static Declaration Describe(GeneratorAttributeSyntaxContext context)
    {
        var property = (IPropertySymbol)context.TargetSymbol;
        var syntax = (PropertyDeclarationSyntax)context.TargetNode;
        string error = "";
        ITypeSymbol value = property.Type;
        bool nullableValue = value is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
        if (nullableValue)
        {
            value = ((INamedTypeSymbol)value).TypeArguments[0];
        }

        string kind = "scalar";
        string element = "";
        if (value is INamedTypeSymbol collection)
        {
            string definition = collection.OriginalDefinition.ToDisplayString();
            if (definition == "System.Collections.Generic.IReadOnlyList<T>")
            {
                kind = "list";
                element = collection.TypeArguments[0].ToDisplayString(s_typeFormat);
            }
            else if (definition == "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
                && collection.TypeArguments[0].SpecialType == SpecialType.System_String)
            {
                kind = "map";
                element = collection.TypeArguments[1].ToDisplayString(s_typeFormat);
            }
        }

        string valueType = value.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(s_typeFormat);
        bool supported = kind switch
        {
            "list" => element is "string" or "global::Incant.CX.Arguments.RawArgument"
                or "global::Incant.CX.Arguments.LinkInput" or "global::Incant.CX.Arguments.Sanitizer",
            "map" => element is "string?" or "bool",
            _ => value.SpecialType is SpecialType.System_Boolean or SpecialType.System_Int32
                    or SpecialType.System_Int64 or SpecialType.System_String
                || valueType is "global::System.Version" or "global::Incant.CX.Arguments.Pch"
                    or "global::Incant.CX.Arguments.Dependencies"
                || value.TypeKind == TypeKind.Enum && value.ContainingNamespace.ToDisplayString()
                    is "Incant.CX" or "Incant.CX.Arguments",
        };
        if (property.ContainingType.ToDisplayString() != Owner || property.ContainingType.TypeKind != TypeKind.Class
            || !property.ContainingType.IsSealed || property.ContainingType.IsRecord || property.ContainingType.Arity != 0
            || property.IsStatic || property.IsIndexer || property.Name == "value__"
            || SyntaxFacts.GetKeywordKind(property.Name) != SyntaxKind.None
            || property.DeclaredAccessibility != Accessibility.Public || property.SetMethod is not null
            || property.GetMethod is null || !syntax.Modifiers.Any(SyntaxKind.PartialKeyword)
            || syntax.AccessorList?.Accessors.Any(accessor => accessor.Body is not null || accessor.ExpressionBody is not null) != false
            || (!nullableValue && property.NullableAnnotation != NullableAnnotation.Annotated) || !supported)
        {
            error = $"'{property.Name}' must be a public, optional, get-only partial property of {Owner} with a supported immutable CX value type.";
        }

        string[] methods = kind == "scalar" ? new[] { "With", "Without" } : new[] { "With", "Without", "Append", "Remove" };
        string conflict = methods.Select(prefix => prefix + property.Name)
            .FirstOrDefault(name => property.ContainingType.GetMembers(name).Length > 0) ?? "";
        INamedTypeSymbol? generationContext = context.SemanticModel.Compilation.GetTypeByMetadataName(
            "Incant.CX.Arguments.GenerationContext");
        if (generationContext?.GetMembers(property.Name).Length > 0)
        {
            conflict = "GenerationContext." + property.Name;
        }

        if (property.ContainingNamespace.GetTypeMembers("ArgumentField").Length > 0)
        {
            conflict = "ArgumentField";
        }

        return new Declaration(property.Name, valueType, property.Type.ToDisplayString(s_typeFormat),
            kind, element, value.IsReferenceType, error, conflict);
    }

    private static void Emit(SourceProductionContext context, ImmutableArray<Declaration> declarations)
    {
        foreach (Declaration field in declarations)
        {
            if (field.Error.Length > 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(s_invalid, Location.None, field.Error));
            }

            if (field.Conflict.Length > 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(s_conflict, Location.None, field.Conflict));
            }
        }

        var duplicates = new HashSet<string>(declarations.GroupBy(field => field.Name)
            .Where(group => group.Count() > 1).Select(group => group.Key), StringComparer.Ordinal);
        foreach (string duplicate in duplicates)
        {
            context.ReportDiagnostic(Diagnostic.Create(s_conflict, Location.None, duplicate));
        }

        Declaration[] fields = declarations.Where(field => field.Error.Length == 0 && field.Conflict.Length == 0
            && !duplicates.Contains(field.Name)).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
        if (fields.Length == 0)
        {
            return;
        }

        var source = new CodeBuilder();
        source.Line("// <auto-generated/>");
        source.Line("#nullable enable");
        source.Line("using System.Linq;");
        source.Line("namespace Incant.CX.Arguments;");
        source.Line();
        EmitFields(source, fields);
        source.Line();
        source.Line("public sealed partial class ArgumentSet");
        source.Line("{");
        using (source.IndentScope())
        {
            foreach (Declaration field in fields)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                EmitProperty(source, field);
            }

            EmitFieldOperations(source, fields);
        }

        source.Line("}");
        source.Line();
        EmitReadTracking(source, fields);
        context.AddSource("ArgumentSet.g.cs", SourceText.From(source.Content, Encoding.UTF8));
    }

    private static void EmitFields(CodeBuilder source, Declaration[] fields)
    {
        source.Line("/// <summary>Identifies a fixed C-family configuration field.</summary>");
        source.Line("public enum ArgumentField");
        source.Line("{");
        using (source.IndentScope())
        {
            foreach (Declaration field in fields)
            {
                source.Line($"/// <summary>The {field.Name} configuration.</summary>");
                source.Line($"{field.Name},");
            }
        }

        source.Line("}");
    }

    private static void EmitFieldOperations(CodeBuilder source, Declaration[] fields)
    {
        source.Line("private static object MergeValue(ArgumentField field, object left, object right) => field switch");
        source.Line("{");
        using (source.IndentScope())
        {
            foreach (Declaration field in fields.Where(field => field.Kind != "scalar"))
            {
                string left = $"(({field.ValueType})left)";
                string right = $"({field.ValueType})right";
                string merged = field.Kind == "list"
                    ? $"global::System.Array.AsReadOnly({left}.Concat({right}).ToArray())"
                    : $"MergeMap({left}, {right})";
                source.Line($"ArgumentField.{field.Name} => {merged},");
            }

            source.Line("_ => left.Equals(right) ? left : throw new global::System.ArgumentException(\"Values must agree.\"),");
        }

        source.Line("};");
        source.Line("private static int ElementCount(ArgumentField field, object value) => field switch");
        source.Line("{");
        using (source.IndentScope())
        {
            foreach (Declaration field in fields.Where(field => field.Kind != "scalar"))
            {
                source.Line($"ArgumentField.{field.Name} => (({field.ValueType})value).Count,");
            }

            source.Line("_ => 0,");
        }

        source.Line("};");
    }

    private static void EmitReadTracking(CodeBuilder source, Declaration[] fields)
    {
        // Read access is typed and records use so an applicable but unhandled field cannot disappear silently.
        source.Line("internal sealed partial class GenerationContext");
        source.Line("{");
        using (source.IndentScope())
        {
            foreach (Declaration field in fields)
            {
                source.Line($"internal {field.PropertyType} {field.Name}");
                source.Line("{");
                using (source.IndentScope())
                {
                    source.Line("get");
                    source.Line("{");
                    using (source.IndentScope())
                    {
                        source.Line($"_consumed.Add(ArgumentField.{field.Name});");
                        source.Line($"return Values.{field.Name};");
                    }

                    source.Line("}");
                }

                source.Line("}");
            }
        }

        source.Line("}");
    }

    private static void EmitProperty(CodeBuilder source, Declaration field)
    {
        string selector = $"ArgumentField.{field.Name}";
        source.Line($"public partial {field.PropertyType} {field.Name} => ({field.PropertyType})ReadValue({selector});");
        source.Line($"/// <summary>Replaces {field.Name} with a new original contribution.</summary>");
        source.Line($"public ArgumentSet With{field.Name}({field.ValueType} value)");
        source.Line("{");
        using (source.IndentScope())
        {
            if (field.IsReference)
            {
                source.Line("global::System.ArgumentNullException.ThrowIfNull(value);");
            }

            string snapshot = field.Kind == "list" ? "SnapshotList(value)" : field.Kind == "map" ? "SnapshotMap(value)" : "value";
            source.Line($"return Set({selector}, {snapshot});");
        }

        source.Line("}");
        source.Line($"/// <summary>Explicitly removes {field.Name}.</summary>");
        source.Line($"public ArgumentSet Without{field.Name}() => Set({selector}, null, removed: true);");
        if (field.Kind == "scalar")
        {
            return;
        }

        string parameter = field.Kind == "list" ? $"params {field.Element}[]" : field.ValueType;
        string appended = field.Kind == "list" ? "SnapshotList(values)" : "SnapshotMap(values)";
        source.Line($"/// <summary>Appends a fresh contribution to {field.Name}.</summary>");
        source.Line($"public ArgumentSet Append{field.Name}({parameter} values) => AppendValue({selector}, {appended});");
        string removed = field.Kind == "list" ? "elements" : "names";
        string predicateType = field.Kind == "list" ? field.Element : "string";
        source.Line($"/// <summary>Removes matching {removed} as an explicit replacement.</summary>");
        source.Line($"public ArgumentSet Remove{field.Name}(global::System.Predicate<{predicateType}> predicate)");
        source.Line("{");
        using (source.IndentScope())
        {
            source.Line("global::System.ArgumentNullException.ThrowIfNull(predicate);");
            string filtered = field.Kind == "list"
                ? $"{field.Name}.Where(value => !predicate(value)).ToArray()"
                : $"{field.Name}.Where(pair => !predicate(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value)";
            source.Line($"return {field.Name} is null ? Filter(_ => true) : With{field.Name}({filtered});");
        }

        source.Line("}");
    }

    // Keep the generator independent of runtime assemblies and use stable newlines on every host.
    private sealed class CodeBuilder
    {
        private readonly StringBuilder _content = new StringBuilder();
        private int _indent;

        internal string Content => _content.ToString();

        internal void Line(string text = "")
        {
            if (text.Length > 0)
            {
                _content.Append(' ', _indent * 4);
                _content.Append(text);
            }

            _content.Append('\n');
        }

        internal IDisposable IndentScope()
        {
            _indent++;
            return new IndentationScope(this);
        }

        private sealed class IndentationScope(CodeBuilder builder) : IDisposable
        {
            private CodeBuilder? _builder = builder;

            public void Dispose()
            {
                if (_builder is null)
                {
                    return;
                }

                _builder._indent--;
                _builder = null;
            }
        }
    }

    private sealed class Declaration : IEquatable<Declaration>
    {
        internal Declaration(string name, string valueType, string propertyType, string kind,
            string element, bool isReference, string error, string conflict)
        {
            Name = name;
            ValueType = valueType;
            PropertyType = propertyType;
            Kind = kind;
            Element = element;
            IsReference = isReference;
            Error = error;
            Conflict = conflict;
        }

        internal string Name { get; }

        internal string ValueType { get; }

        internal string PropertyType { get; }

        internal string Kind { get; }

        internal string Element { get; }

        internal bool IsReference { get; }

        internal string Error { get; }

        internal string Conflict { get; }

        public bool Equals(Declaration? other) => other is not null && Name == other.Name
            && ValueType == other.ValueType && PropertyType == other.PropertyType && Kind == other.Kind
            && Element == other.Element && IsReference == other.IsReference && Error == other.Error && Conflict == other.Conflict;

        public override bool Equals(object? obj) => obj is Declaration other && Equals(other);

        public override int GetHashCode() => Name.GetHashCode() ^ ValueType.GetHashCode() ^ Kind.GetHashCode();
    }
}
