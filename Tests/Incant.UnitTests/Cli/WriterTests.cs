using Incant.Base.Cli;

namespace Incant.UnitTests.Cli;

public sealed class WriterTests
{
    [Fact]
    public void NewWriterIsEmpty()
    {
        var writer = new Writer();

        Assert.True(writer.IsEmpty);
        Assert.Equal(string.Empty, writer.Content);
    }

    [Fact]
    public void WriteMethodsAppendContentAndPlatformNewLines()
    {
        var writer = new Writer();

        writer.Write("first").NextLine().WriteLine("second").Write("third");

        Assert.Equal($"first{Environment.NewLine}second{Environment.NewLine}third", writer.Content);
    }

    [Fact]
    public void WriteKeepIndentAlignsContinuationLinesWithCurrentColumn()
    {
        var writer = new Writer();

        writer.Write("prefix: ").WriteKeepIndent("first\nsecond");

        Assert.Equal("prefix: first\n        second", writer.Content);
    }

    [Fact]
    public void NewLineResetsIndentUsedByWriteKeepIndent()
    {
        var writer = new Writer();

        writer.WriteLine("prefix").WriteKeepIndent("first\nsecond");

        Assert.Equal($"prefix{Environment.NewLine}first\nsecond", writer.Content);
    }

    [Fact]
    public void StyleMethodsAppendAnsiEscapeSequencesInOrder()
    {
        var writer = new Writer();

        writer
            .StyleClear()
            .StyleBold()
            .StyleNoBold()
            .StyleUnderline()
            .StyleNoUnderline()
            .StyleReverse()
            .StyleNoReverse()
            .StyleFrontGray()
            .StyleFrontRed()
            .StyleFrontGreen()
            .StyleFrontYellow()
            .StyleFrontBlue()
            .StyleFrontMagenta()
            .StyleFrontCyan()
            .StyleFrontWhite()
            .StyleBackGray()
            .StyleBackRed()
            .StyleBackGreen()
            .StyleBackYellow()
            .StyleBackBlue()
            .StyleBackMagenta()
            .StyleBackCyan()
            .StyleBackWhite();

        Assert.Equal(
            "\u001b[0m\u001b[1m\u001b[22m\u001b[4m\u001b[24m\u001b[7m\u001b[27m"
                + "\u001b[30m\u001b[31m\u001b[32m\u001b[33m\u001b[34m\u001b[35m\u001b[36m\u001b[37m"
                + "\u001b[40m\u001b[41m\u001b[42m\u001b[43m\u001b[44m\u001b[45m\u001b[46m\u001b[47m",
            writer.Content);
    }
}
