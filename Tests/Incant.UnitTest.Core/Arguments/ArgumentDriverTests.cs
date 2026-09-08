using Incant.Core.Arguments;

namespace Incant.UnitTest.Core.Arguments;

public sealed class ArgumentDriverTests
{
    [Fact]
    public void GeneratedMapOperationsPreserveTheMergePolicyAndRemovalSemantics()
    {
        ArgumentSet settings = new ArgumentSet().WithAnnotations(new Dictionary<string, string?> { ["a"] = null })
            .AppendAnnotations(new Dictionary<string, string?> { ["b"] = "second" });
        Assert.Equal(2, settings.Get(ExampleArguments.Annotations).Count);
        ArgumentSet removed = settings.RemoveAnnotations(name => name == "a");
        Assert.Equal("second", Assert.Single(removed.Get(ExampleArguments.Annotations)).Value);
        Assert.Equal(2, settings.Get(ExampleArguments.Annotations).Count);
        Assert.Throws<ArgumentConflictException>(() => settings.AppendAnnotations(
            new Dictionary<string, string?> { ["b"] = "changed" }));
        Assert.True(removed.WithoutAnnotations().IsRemoved(ExampleArguments.Annotations));
    }

    [Fact]
    public void CustomDriverAndGeneratedApiWorkWithoutAnyToolchainTypes()
    {
        ArgumentSet arguments = new ArgumentSet().WithVerbose(false).AppendMessages("first message", "second")
            .AppendMessages("third").RemoveMessages(value => value == "second");
        IArgumentDriver driver = new MessageDriver();
        ArgumentGenerationResult result = driver.Generate(arguments);
        Assert.True(result.Success);
        Assert.Equal(["first message", "third"], result.Arguments);
        Assert.Empty(driver.Generate(arguments.WithMessages([])).Arguments);
        Assert.False(arguments.WithoutVerbose().TryGet(ExampleArguments.Verbose, out _));
    }

    [Fact]
    public void ErrorsSuppressAllPartialArgumentsAndResultsSnapshotInputLists()
    {
        ArgumentSet arguments = new ArgumentSet().WithVerbose(true);
        var values = new List<string> { "partial" };
        var diagnostics = new List<ArgumentDiagnostic>
        {
            new(ArgumentDiagnosticSeverity.Error, ExampleArguments.Messages.Id, "Missing messages",
                arguments.Origins(ExampleArguments.Verbose)),
        };
        var result = new ArgumentGenerationResult(values, diagnostics);
        values.Add("later");
        diagnostics.Clear();
        Assert.False(result.Success);
        Assert.Empty(result.Arguments);
        Assert.Equal(arguments.Id, Assert.Single(Assert.Single(result.Diagnostics).Origins).SetId);
    }

    private sealed class MessageDriver : IArgumentDriver
    {
        public ArgumentGenerationResult Generate(ArgumentSet arguments)
        {
            var values = new List<string>();
            if (arguments.TryGet(ExampleArguments.Verbose, out bool verbose) && verbose)
            {
                values.Add("--verbose");
            }

            if (arguments.TryGet(ExampleArguments.Messages, out IReadOnlyList<string>? messages))
            {
                values.AddRange(messages);
            }

            return new ArgumentGenerationResult(values);
        }
    }
}
