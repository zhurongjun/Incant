using Incant.Base;

namespace Incant.UnitTest.Base;

public sealed class CodeBuilderTests
{
    [Fact]
    public void NewBuilderIsEmptyWithFourSpaceIndentation()
    {
        var builder = new CodeBuilder();

        Assert.True(builder.IsEmpty);
        Assert.Equal(4U, builder.IndentUnit);
        Assert.Equal(string.Empty, builder.Content);
        Assert.Equal(string.Empty, builder.ToString());
    }

    [Fact]
    public void LineUsesConfiguredIndentationForTextAndEmptyLines()
    {
        var builder = new CodeBuilder
        {
            IndentUnit = 2
        };

        builder.Line("root");
        builder.PushIndent();
        builder.Line("child");
        builder.Line();
        builder.PopIndent();

        Assert.Equal(
            $"root{Environment.NewLine}"
                + $"  child{Environment.NewLine}"
                + $"  {Environment.NewLine}",
            builder.Content);
        Assert.False(builder.IsEmpty);
        Assert.Equal(builder.Content, builder.ToString());
    }

    [Fact]
    public void LineIndentRestoresThePreviousIndentation()
    {
        var builder = new CodeBuilder
        {
            IndentUnit = 2
        };
        builder.PushIndent();

        builder.LineIndent(2, "nested");
        builder.Line("child");

        Assert.Equal(
            $"      nested{Environment.NewLine}"
                + $"  child{Environment.NewLine}",
            builder.Content);
    }

    [Fact]
    public void PushIndentAlwaysEntersOneLevel()
    {
        var builder = new CodeBuilder
        {
            IndentUnit = 2
        };

        builder.PushIndent(3);
        builder.Line("value");

        Assert.Equal($"  value{Environment.NewLine}", builder.Content);
    }

    [Fact]
    public void IndentScopeRestoresThePreviousIndentation()
    {
        var builder = new CodeBuilder();

        using (builder.IndentScope())
        {
            builder.Line("nested");
        }
        builder.Line("root");

        Assert.Equal(
            $"    nested{Environment.NewLine}root{Environment.NewLine}",
            builder.Content);
    }

    [Fact]
    public void PopIndentAtRootThrowsWithoutChangingOutput()
    {
        var builder = new CodeBuilder();

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(builder.PopIndent);
        builder.Line("root");

        Assert.Equal("No indent level to pop.", exception.Message);
        Assert.Equal($"root{Environment.NewLine}", builder.Content);
    }

    [Fact]
    public void WriteKeepIndentAtRootPreservesTheOriginalText()
    {
        var builder = new CodeBuilder();
        const string Text = "first\r\nsecond\rthird\n";

        builder.WriteKeepIndent(Text);

        Assert.Equal(Text, builder.Content);
    }

    [Fact]
    public void WriteKeepIndentAtNestedLevelIndentsEverySplitLine()
    {
        var builder = new CodeBuilder
        {
            IndentUnit = 2
        };
        builder.PushIndent();

        builder.WriteKeepIndent("first\r\nsecond\rthird\n");

        Assert.Equal(
            $"  first{Environment.NewLine}"
                + $"  second{Environment.NewLine}"
                + $"  third{Environment.NewLine}"
                + $"  {Environment.NewLine}",
            builder.Content);
    }

    [Fact]
    public void WriteBlockAnnotationUsesCustomDecorationOptions()
    {
        var builder = new CodeBuilder();
        var options = new BlockAnnotationOptions("#", '*', 1, 2);

        builder.WriteBlockAnnotation("a\nbbb", options);

        Assert.Equal(
            $"#*********{Environment.NewLine}"
                + $"#*  a    *{Environment.NewLine}"
                + $"#*  bbb  *{Environment.NewLine}"
                + $"#*********{Environment.NewLine}",
            builder.Content);
    }

    [Fact]
    public void WriteBlockAnnotationLeftAlignsMixedNewlineContentToTheLongestLine()
    {
        var builder = new CodeBuilder();

        builder.WriteBlockAnnotation("a\r\nlongest\rempty next\n", BlockAnnotationOptions.CSharp);

        Assert.Equal(
            $"//!!!!!!!!!!!!!!!!{Environment.NewLine}"
                + $"//!! a          !!{Environment.NewLine}"
                + $"//!! longest    !!{Environment.NewLine}"
                + $"//!! empty next !!{Environment.NewLine}"
                + $"//!!            !!{Environment.NewLine}"
                + $"//!!!!!!!!!!!!!!!!{Environment.NewLine}",
            builder.Content);
    }

    [Fact]
    public void WriteBlockAnnotationUsesTheCurrentIndentation()
    {
        var builder = new CodeBuilder
        {
            IndentUnit = 2
        };
        builder.PushIndent();

        builder.WriteBlockAnnotation("x", BlockAnnotationOptions.Python);

        Assert.Equal(
            $"  #!!!!!!!{Environment.NewLine}"
                + $"  #!! x !!{Environment.NewLine}"
                + $"  #!!!!!!!{Environment.NewLine}",
            builder.Content);
    }

    [Fact]
    public void WriteBlockAnnotationSupportsEmptyContentAndZeroSpacing()
    {
        var builder = new CodeBuilder();
        var options = new BlockAnnotationOptions("--", '!', 0, 0);

        builder.WriteBlockAnnotation(string.Empty, options);

        Assert.Equal(
            $"--{Environment.NewLine}--{Environment.NewLine}--{Environment.NewLine}",
            builder.Content);
    }

    [Fact]
    public void WriteBlockAnnotationRejectsNullText()
    {
        var builder = new CodeBuilder();

        Assert.Throws<ArgumentNullException>(
            () => builder.WriteBlockAnnotation(null!, BlockAnnotationOptions.CSharp));
        Assert.True(builder.IsEmpty);
    }

    [Fact]
    public void WriteBlockAnnotationRejectsNullOptions()
    {
        var builder = new CodeBuilder();

        Assert.Throws<ArgumentNullException>(() => builder.WriteBlockAnnotation("text", null!));
        Assert.True(builder.IsEmpty);
    }

    [Fact]
    public void WriteBlockAnnotationRejectsAWidthBeyondTheSupportedRange()
    {
        var builder = new CodeBuilder();
        var options = new BlockAnnotationOptions("//", '!', int.MaxValue, 0);

        Assert.Throws<OverflowException>(() => builder.WriteBlockAnnotation("text", options));
        Assert.True(builder.IsEmpty);
    }

    [Fact]
    public void WriteGenerateNoteUsesTheCSharpBannerLayout()
    {
        var builder = new CodeBuilder();

        builder.WriteGenerateNote(BlockAnnotationOptions.CSharp);

        Assert.Equal(
            $"//!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!{Environment.NewLine}"
                + $"//!! THIS FILE IS GENERATED, ANY CHANGES WILL BE LOST !!{Environment.NewLine}"
                + $"//!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!{Environment.NewLine}"
                + Environment.NewLine,
            builder.Content);
    }

    [Fact]
    public void WriteGenerateNoteUsesTheSuppliedAnnotationOptions()
    {
        var builder = new CodeBuilder();
        var options = new BlockAnnotationOptions(";", '*', 1, 0);
        const string Note = "THIS FILE IS GENERATED, ANY CHANGES WILL BE LOST";
        string border = ";" + new string('*', Note.Length + 2);

        builder.WriteGenerateNote(options);

        Assert.Equal(
            $"{border}{Environment.NewLine}"
                + $";*{Note}*{Environment.NewLine}"
                + $"{border}{Environment.NewLine}"
                + Environment.NewLine,
            builder.Content);
    }

    [Fact]
    public void WriteGenerateNoteRejectsNullOptions()
    {
        var builder = new CodeBuilder();

        Assert.Throws<ArgumentNullException>(() => builder.WriteGenerateNote(null!));
        Assert.True(builder.IsEmpty);
    }

    [Fact]
    public void ResetClearsContentAndIndentation()
    {
        var builder = new CodeBuilder();
        builder.PushIndent();
        builder.Line("nested");

        builder.Reset();

        Assert.True(builder.IsEmpty);
        Assert.Equal(string.Empty, builder.Content);

        builder.Line("root");

        Assert.Equal($"root{Environment.NewLine}", builder.Content);
    }
}
