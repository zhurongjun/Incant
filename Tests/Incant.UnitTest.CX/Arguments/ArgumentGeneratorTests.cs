using Incant.CX.Arguments;
using Incant.CX.Arguments.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Incant.UnitTest.CX.Arguments;

public sealed class ArgumentGeneratorTests
{
    [Theory]
    [InlineData("[Argument] public bool Flag { get; }", "ArgumentSet")]
    [InlineData("[Argument] public partial object? Flag { get; }", "ArgumentSet")]
    [InlineData("[Argument] public partial System.Collections.Generic.List<string>? Flag { get; }", "ArgumentSet")]
    [InlineData("[Argument] public partial bool? @event { get; }", "ArgumentSet")]
    [InlineData("[Argument] public partial bool? Flag { get; }", "Other")]
    public void InvalidOrExternalDeclarationsProduceCompilerDiagnostics(string member, string owner)
    {
        string source = Declaration(owner, member);
        Assert.Contains(Generate(source), diagnostic => diagnostic.Id == "INCARG001");
    }

    [Fact]
    public void ExistingTransformationNamesAreRejected()
    {
        string source = Declaration("ArgumentSet", """
            [Argument] public partial bool? Flag { get; }
            public ArgumentSet WithFlag(bool value) => this;
            """);
        Assert.Contains(Generate(source), diagnostic => diagnostic.Id == "INCARG002");
    }

    [Fact]
    public void GeneratedReadTrackingCannotShadowContextMembers()
    {
        string source = Declaration("ArgumentSet", "[Argument] public partial bool? Flag { get; }")
            + "internal sealed partial class GenerationContext { internal bool Flag { get; } }";
        Assert.Contains(Generate(source), diagnostic => diagnostic.Id == "INCARG002");
    }

    [Fact]
    public void DuplicateFixedPropertiesAreRejected()
    {
        string source = Declaration("ArgumentSet", """
            [Argument] public partial bool? Flag { get; }
            [Argument] public partial bool? Flag { get; }
            """);
        Assert.Contains(Generate(source), diagnostic => diagnostic.Id == "INCARG002");
    }

    [Fact]
    public void ConsumerCompilesAgainstGeneratedPropertiesAndTransformations()
    {
        const string Source = """
            #nullable enable
            using System.Collections.Generic;
            using Incant.CX.Arguments;
            public static class Consumer
            {
                public static ArgumentSet Create() => new ArgumentSet().WithStandard("c++17")
                    .WithDefines(new Dictionary<string, string?> { ["a"] = null })
                    .AppendDefines(new Dictionary<string, string?> { ["b"] = "" })
                    .RemoveDefines(name => name == "a").AppendInputs("file.cpp").WithoutStandard();
                public static string? Standard(ArgumentSet settings) => settings.Standard;
            }
            """;
        CSharpCompilation compilation = CreateCompilation(Source, includeCx: true);
        Assert.DoesNotContain(compilation.GetDiagnostics(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.Severity is Microsoft.CodeAnalysis.DiagnosticSeverity.Error or Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
        using var output = new MemoryStream();
        Assert.True(compilation.Emit(output, cancellationToken: TestContext.Current.CancellationToken).Success);
    }

    private static string Declaration(string owner, string member) => """
        #nullable enable
        namespace Incant.CX.Arguments;
        [System.AttributeUsage(System.AttributeTargets.Property)]
        internal sealed class ArgumentAttribute : System.Attribute { }
        public sealed partial class
        """ + " " + owner + " { " + member + " }";

    private static IReadOnlyList<Diagnostic> Generate(string source)
    {
        CSharpCompilation compilation = CreateCompilation(source, includeCx: false);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ArgumentSetGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out System.Collections.Immutable.ImmutableArray<Diagnostic> diagnostics,
            TestContext.Current.CancellationToken);
        return diagnostics;
    }

    private static CSharpCompilation CreateCompilation(string source, bool includeCx)
    {
        string[] trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        IEnumerable<string> paths = trusted.Where(path => !Path.GetFileName(path).StartsWith("Incant.", StringComparison.Ordinal));
        if (includeCx)
        {
            paths = paths.Append(typeof(ArgumentSet).Assembly.Location);
        }

        IEnumerable<MetadataReference> references = paths.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create("ArgumentConsumer", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
    }
}
