using Incant.Base;

namespace Incant.UnitTest.Base;

public sealed class BlockAnnotationOptionsTests
{
    [Fact]
    public void ConstructorExposesEveryConfigurationValue()
    {
        var options = new BlockAnnotationOptions(";", '*', 2, 3);

        Assert.Equal(";", options.LineCommentPrefix);
        Assert.Equal('*', options.FillCharacter);
        Assert.Equal(2, options.FillThickness);
        Assert.Equal(3, options.Padding);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//\r")]
    [InlineData("//\n")]
    public void ConstructorRejectsInvalidLineCommentPrefixes(string? lineCommentPrefix)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new BlockAnnotationOptions(lineCommentPrefix!, '!', 1, 1));
    }

    [Theory]
    [InlineData('\r')]
    [InlineData('\n')]
    public void ConstructorRejectsLineBreakFillCharacters(char fillCharacter)
    {
        Assert.Throws<ArgumentException>(
            () => new BlockAnnotationOptions("//", fillCharacter, 1, 1));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void ConstructorRejectsNegativeDimensions(
        int fillThickness,
        int padding)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BlockAnnotationOptions("//", '!', fillThickness, padding));
    }

    [Fact]
    public void LanguagePresetsUseTheirLineCommentPrefixAndDefaultDecoration()
    {
        (BlockAnnotationOptions Options, string Prefix)[] presets =
        [
            (BlockAnnotationOptions.C, "//"),
            (BlockAnnotationOptions.Cpp, "//"),
            (BlockAnnotationOptions.CSharp, "//"),
            (BlockAnnotationOptions.Java, "//"),
            (BlockAnnotationOptions.JavaScript, "//"),
            (BlockAnnotationOptions.TypeScript, "//"),
            (BlockAnnotationOptions.Python, "#"),
            (BlockAnnotationOptions.Shell, "#"),
            (BlockAnnotationOptions.PowerShell, "#"),
            (BlockAnnotationOptions.Lua, "--"),
            (BlockAnnotationOptions.Sql, "--")
        ];

        foreach ((BlockAnnotationOptions options, string prefix) in presets)
        {
            Assert.Equal(prefix, options.LineCommentPrefix);
            Assert.Equal('!', options.FillCharacter);
            Assert.Equal(2, options.FillThickness);
            Assert.Equal(1, options.Padding);
        }
    }
}
