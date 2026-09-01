using Incant.Base.Cli;

namespace Incant.UnitTest.Base.Cli;

public sealed class TokenTests
{
    [Theory]
    [InlineData("", Token.TokenKind.Argument)]
    [InlineData("-", Token.TokenKind.Argument)]
    [InlineData("-0", Token.TokenKind.Argument)]
    [InlineData("-9", Token.TokenKind.Argument)]
    [InlineData("-1.25", Token.TokenKind.Argument)]
    [InlineData("-1e3", Token.TokenKind.Argument)]
    [InlineData("target", Token.TokenKind.Argument)]
    [InlineData("--output", Token.TokenKind.Option)]
    [InlineData("---", Token.TokenKind.Option)]
    [InlineData("--=value", Token.TokenKind.Option)]
    [InlineData("-o", Token.TokenKind.ShortOption)]
    [InlineData("-.5", Token.TokenKind.ShortOption)]
    [InlineData("-9x", Token.TokenKind.ShortOption)]
    [InlineData("-=value", Token.TokenKind.ShortOption)]
    [InlineData("--", Token.TokenKind.DoubleDash)]
    public void ParseClassifiesRawToken(string raw, Token.TokenKind expectedKind)
    {
        Token token = Token.Parse(raw);

        Assert.Equal(expectedKind, token.Kind);
        Assert.Equal(raw, token.Raw);
    }

    [Fact]
    public void LongOptionExposesNameAndOptionalValue()
    {
        Token optionWithoutValue = Token.Parse("--output");
        Token optionWithEmptyValue = Token.Parse("--output=");
        Token optionWithValue = Token.Parse("--output=build=debug");

        Assert.Equal("output", optionWithoutValue.OptionName);
        Assert.Null(optionWithoutValue.OptionValue);
        Assert.Equal("output", optionWithEmptyValue.OptionName);
        Assert.Equal(string.Empty, optionWithEmptyValue.OptionValue);
        Assert.Equal("output", optionWithValue.OptionName);
        Assert.Equal("build=debug", optionWithValue.OptionValue);
    }

    [Fact]
    public void ShortOptionExposesNamesAndOptionalValue()
    {
        Token optionWithoutValue = Token.Parse("-a");
        Token optionWithEmptyValue = Token.Parse("-a=");
        Token token = Token.Parse("-abc=value=tail");

        Assert.Equal(['a'], optionWithoutValue.ShortOptionNames);
        Assert.Null(optionWithoutValue.ShortOptionValue);
        Assert.Equal(['a'], optionWithEmptyValue.ShortOptionNames);
        Assert.Equal(string.Empty, optionWithEmptyValue.ShortOptionValue);
        Assert.Equal(['a', 'b', 'c'], token.ShortOptionNames);
        Assert.Equal("value=tail", token.ShortOptionValue);
        Assert.True(token.HasShortOption('b'));
        Assert.False(token.IsShortOptionOf('b'));
    }

    [Fact]
    public void OptionPredicatesRequireTheExpectedKindAndExactName()
    {
        Token longOption = Token.Parse("--output=value");
        Token shortOption = Token.Parse("-o");
        Token shortGroup = Token.Parse("-abc");
        Token argument = Token.Parse("output");

        Assert.True(longOption.IsOptionOf("output"));
        Assert.False(longOption.IsOptionOf("Output"));
        Assert.False(argument.IsOptionOf("output"));
        Assert.True(shortOption.IsShortOptionOf('o'));
        Assert.False(shortOption.IsShortOptionOf('O'));
        Assert.False(shortGroup.IsShortOptionOf('a'));
        Assert.True(shortGroup.HasShortOption('b'));
        Assert.False(argument.HasShortOption('b'));
    }

    [Fact]
    public void KindSpecificAccessorsRejectOtherTokenKinds()
    {
        Token argument = Token.Parse("target");
        Token option = Token.Parse("--output");

        Assert.Throws<InvalidOperationException>(() => _ = argument.OptionName);
        Assert.Throws<InvalidOperationException>(() => _ = argument.OptionValue);
        Assert.Throws<InvalidOperationException>(() => _ = argument.ShortOptionNames);
        Assert.Throws<InvalidOperationException>(() => _ = argument.ShortOptionValue);
        Assert.Throws<InvalidOperationException>(() => _ = option.Argument);
        Assert.Throws<InvalidOperationException>(() => _ = option.ShortOptionNames);
        Assert.Throws<InvalidOperationException>(() => _ = option.ShortOptionValue);
    }

    [Fact]
    public void UnparsedFactoryPreservesRawToken()
    {
        Token token = Token.Unparsed("--literal");

        Assert.True(token.IsUnparsed);
        Assert.False(token.IsArgument);
        Assert.False(token.IsAnyOption);
        Assert.False(token.IsDoubleDash);
        Assert.Equal("--literal", token.Raw);
    }
}
