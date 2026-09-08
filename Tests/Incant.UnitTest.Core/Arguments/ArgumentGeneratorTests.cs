using Incant.Arguments.Generator;
using Incant.Core.Arguments;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Incant.UnitTest.Core.Arguments;

public sealed class ArgumentGeneratorTests
{
    [Theory]
    [InlineData("public static ArgumentKey<bool> Flag = ArgumentKeys.Scalar<bool>(\"flag\");", "INCARG001")]
    [InlineData("public static readonly bool Flag = true;", "INCARG001")]
    public void InvalidDeclarationsProduceCompilerDiagnostics(string member, string expected)
    {
        string source = "using Incant.Core.Arguments; public static class Settings { [GenerateArgument] " + member + " }";
        ImmutableResult result = Generate(source);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == expected);
    }

    [Fact]
    public void DuplicateExtensionNamesAreRejectedAcrossTypesInTheSameNamespace()
    {
        const string Source = """
            using Incant.Core.Arguments;
            namespace Example;
            public static class First
            {
                [GenerateArgument("Level")]
                public static ArgumentKey<int> Value { get; } = ArgumentKeys.Scalar<int>("first");
            }
            public static class Second
            {
                [GenerateArgument("Level")]
                public static ArgumentKey<int> Value { get; } = ArgumentKeys.Scalar<int>("second");
            }
            """;
        Assert.Contains(Generate(Source).Diagnostics, diagnostic => diagnostic.Id == "INCARG002");
    }

    [Fact]
    public void GeneratedNullableMapApiCompilesAsAConsumerWithoutReflection()
    {
        const string Source = """
            #nullable enable
            using System.Collections.Generic;
            using Incant.Core.Arguments;
            namespace Example;
            public static class Settings
            {
                [GenerateArgument]
                public static ArgumentKey<IReadOnlyDictionary<string, string?>> Names { get; } =
                    ArgumentKeys.Map<string?>("names", value => value);
            }
            public static class Consumer
            {
                public static ArgumentSet Create() => new ArgumentSet().WithNames(
                    new Dictionary<string, string?> { ["a"] = null }).WithoutNames();
            }
            """;
        ImmutableResult result = Generate(Source);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(TestContext.Current.CancellationToken), diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning);
        using var output = new MemoryStream();
        Assert.True(result.Compilation.Emit(output, cancellationToken: TestContext.Current.CancellationToken).Success);
    }

    private static ImmutableResult Generate(string source)
    {
        string[] trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        IEnumerable<MetadataReference> references = trusted.Append(typeof(ArgumentSet).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path));
        CSharpCompilation compilation = CSharpCompilation.Create("ArgumentConsumer",
            [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ArgumentExtensionsGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation updated, out System.Collections.Immutable.ImmutableArray<Diagnostic> diagnostics);
        return new ImmutableResult(updated, diagnostics);
    }

    private sealed record ImmutableResult(Compilation Compilation, IReadOnlyList<Diagnostic> Diagnostics);
}
