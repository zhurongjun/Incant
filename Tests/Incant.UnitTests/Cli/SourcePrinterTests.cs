using Incant.Base.Cli;

namespace Incant.UnitTests.Cli;

public sealed class SourcePrinterTests
{
    [Fact]
    public void ConstructorIndexesSourceLinesWithoutNewLineCharacters()
    {
        var printer = new SourcePrinter("first\nsecond\nthird");

        Assert.Equal("first\nsecond\nthird", printer.Text);
        Assert.Equal(3, printer.Lines.Count);
        Assert.Equal(new SourceLineData { Start = 0, End = 5 }, printer.Lines[0]);
        Assert.Equal(new SourceLineData { Start = 6, End = 12 }, printer.Lines[1]);
        Assert.Equal(new SourceLineData { Start = 13, End = 18 }, printer.Lines[2]);
        Assert.Equal("second", printer.LineAt(1).ToString());
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    [InlineData(6, 1)]
    [InlineData(12, 1)]
    [InlineData(13, 2)]
    [InlineData(18, 2)]
    [InlineData(19, -1)]
    public void SourcePositionToLineIndexMapsPositionsAndNewLineBoundaries(
        int sourcePosition,
        int expectedLineIndex)
    {
        var printer = new SourcePrinter("first\nsecond\nthird");

        Assert.Equal(expectedLineIndex, printer.SourcePositionToLineIndex(sourcePosition));
    }

    [Fact]
    public void EmptySourceContainsNoLines()
    {
        var printer = new SourcePrinter(string.Empty);

        Assert.Empty(printer.Lines);
        Assert.Equal(-1, printer.SourcePositionToLineIndex(0));
    }

    [Fact]
    public void PrintLinesCanIncludeOrOmitLineNumbers()
    {
        var printer = new SourcePrinter("first\nsecond\nthird");
        var numberedWriter = new Writer();
        var plainWriter = new Writer();

        printer.PrintLines(numberedWriter, 1, 2);
        printer.PrintLines(plainWriter, 1, 2, withLineNumber: false);

        Assert.Equal($"2|second{Environment.NewLine}3|third{Environment.NewLine}", numberedWriter.Content);
        Assert.Equal($"second{Environment.NewLine}third{Environment.NewLine}", plainWriter.Content);
    }

    [Fact]
    public void PrintIndicatedSourceSpanIncludesPreviewAndIndicator()
    {
        var printer = new SourcePrinter("alpha\nbeta\ngamma");
        var writer = new Writer();

        printer.PrintIndicatedSourceSpan(writer, start: 7, end: 9, previewLineCount: 1);

        Assert.Equal(
            $"1|alpha{Environment.NewLine}"
                + $"2|beta{Environment.NewLine}"
                + $" |\u001b[32m ^^\u001b[0m{Environment.NewLine}"
                + $"3|gamma{Environment.NewLine}",
            writer.Content);
    }

    [Fact]
    public void PrintIndicatedSourceSpanCanOmitLineNumbers()
    {
        var printer = new SourcePrinter("alpha\nbeta");
        var writer = new Writer();

        printer.PrintIndicatedSourceSpan(
            writer,
            start: 1,
            end: 3,
            previewLineCount: 0,
            withLineNumber: false);

        Assert.Equal($"alpha{Environment.NewLine}\u001b[32m ^^\u001b[0m{Environment.NewLine}", writer.Content);
    }

    [Fact]
    public void PrintIndicatedSourceSpanIgnoresPositionsOutsideSource()
    {
        var printer = new SourcePrinter("alpha");
        var writer = new Writer();

        printer.PrintIndicatedSourceSpan(writer, start: -1, end: 2);
        printer.PrintIndicatedSourceSpan(writer, start: 1, end: 6);

        Assert.True(writer.IsEmpty);
    }
}
