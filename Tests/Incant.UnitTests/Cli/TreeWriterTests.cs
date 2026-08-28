using System.Text;
using Incant.Base.Cli;

namespace Incant.UnitTests.Cli;

public sealed class TreeWriterTests
{
    [Fact]
    public void EmptyWriterBuildsEmptyText()
    {
        var writer = new TreeWriter();

        Assert.Equal(string.Empty, writer.Build());
        Assert.Equal(string.Empty, writer.BuildWithoutSolvingIndent());
    }

    [Fact]
    public void BuildRendersSiblingNodes()
    {
        var writer = new TreeWriter();
        writer.WriteLine("first").WriteLine("second");

        string result = writer.Build();

        Assert.Equal($"|-first{Environment.NewLine}`-second{Environment.NewLine}", result);
    }

    [Fact]
    public void WriteAndEndLineCombineTextIntoOneNode()
    {
        var writer = new TreeWriter();

        writer.Write("first").Write(" second").EndLine();

        Assert.Equal($"`-first second{Environment.NewLine}", writer.Build());
    }

    [Fact]
    public void ParameterlessWriteLineCreatesAnEmptyNode()
    {
        var writer = new TreeWriter();

        writer.WriteLine();

        Assert.Equal($"`-{Environment.NewLine}", writer.Build());
    }

    [Fact]
    public void EndingAtLineStartDoesNotCreateANode()
    {
        var writer = new TreeWriter();

        writer.EndLine().EndLine();

        Assert.Equal(string.Empty, writer.Build());
    }

    [Fact]
    public void IndentScopeRendersNestedNodesAndRestoresParentIndent()
    {
        var writer = new TreeWriter();
        writer.WriteLine("root");
        using (writer.IndentScope())
        {
            writer.WriteLine("first child");
            writer.WriteLine("last child");
        }
        writer.WriteLine("next root");

        string result = writer.Build();

        Assert.Equal(
            $"|-root{Environment.NewLine}"
                + $"| |-first child{Environment.NewLine}"
                + $"| `-last child{Environment.NewLine}"
                + $"`-next root{Environment.NewLine}",
            result);
    }

    [Fact]
    public void NestedScopesRenderEveryIndentLevel()
    {
        var writer = new TreeWriter();
        writer.WriteLine("root");
        using (writer.IndentScope())
        {
            writer.WriteLine("child");
            using (writer.IndentScope())
            {
                writer.WriteLine("grandchild");
            }
        }

        Assert.Equal(
            $"`-root{Environment.NewLine}"
                + $"  `-child{Environment.NewLine}"
                + $"    `-grandchild{Environment.NewLine}",
            writer.Build());
    }

    [Fact]
    public void BuildWithoutSolvingIndentRequiresIndentData()
    {
        var writer = new TreeWriter();
        writer.WriteLine("node");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => writer.BuildWithoutSolvingIndent());

        Assert.Contains("SolveIndent", exception.Message);
    }

    [Fact]
    public void SolveIndentAllowsBuildingWithoutRecalculation()
    {
        var writer = new TreeWriter();
        writer.WriteLine("node");

        writer.SolveIndent();
        string result = writer.BuildWithoutSolvingIndent();

        Assert.Equal($"`-node{Environment.NewLine}", result);
    }

    [Fact]
    public void BuildIsRepeatableAndIncludesLinesAddedAfterward()
    {
        var writer = new TreeWriter();
        writer.WriteLine("first");

        string firstBuild = writer.Build();
        string secondBuild = writer.Build();
        writer.WriteLine("second");
        string thirdBuild = writer.Build();

        Assert.Equal($"`-first{Environment.NewLine}", firstBuild);
        Assert.Equal(firstBuild, secondBuild);
        Assert.Equal($"|-first{Environment.NewLine}`-second{Environment.NewLine}", thirdBuild);
    }

    [Fact]
    public void BuildWithoutSolvingRejectsLinesAddedAfterPreviousSolve()
    {
        var writer = new TreeWriter();
        writer.WriteLine("first");
        writer.SolveIndent();
        writer.WriteLine("second");

        Assert.Throws<InvalidOperationException>(() => writer.BuildWithoutSolvingIndent());
    }

    [Fact]
    public void CustomLineBuilderReceivesContentAndIndentNodes()
    {
        var writer = new TreeWriter();
        writer.WriteLine("root");
        using (writer.IndentScope())
        {
            writer.WriteLine("child");
        }

        string result = writer.Build(BuildLine);

        Assert.Equal($"0:root{Environment.NewLine}1:child{Environment.NewLine}", result);
    }

    [Fact]
    public void StyleMethodsWriteAnsiSequencesAsLineContent()
    {
        var writer = new TreeWriter();

        writer.StyleBold().Write("node").StyleClear().EndLine();

        Assert.Equal($"`-\u001b[1mnode\u001b[0m{Environment.NewLine}", writer.Build());
    }

    private static void BuildLine(TreeWriter.LineData line, StringBuilder builder)
    {
        builder.Append(line.Indent).Append(':').Append(line.Content);
    }
}
