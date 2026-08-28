using Incant.Base.Cli;

namespace Incant.UnitTests.Cli;

public sealed class TokenTests
{
    [Theory]
    [InlineData("target", Token.TokenKind.Argument)]
    [InlineData("--output", Token.TokenKind.Option)]
    [InlineData("-o", Token.TokenKind.ShortOption)]
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
        Token optionWithValue = Token.Parse("--output=build=debug");

        Assert.Equal("output", optionWithoutValue.OptionName);
        Assert.Null(optionWithoutValue.OptionValue);
        Assert.Equal("output", optionWithValue.OptionName);
        Assert.Equal("build=debug", optionWithValue.OptionValue);
    }

    [Fact]
    public void ShortOptionExposesNamesAndOptionalValue()
    {
        Token token = Token.Parse("-abc=value");

        Assert.Equal(['a', 'b', 'c'], token.ShortOptionNames);
        Assert.Equal("value", token.ShortOptionValue);
        Assert.True(token.HasShortOption('b'));
        Assert.False(token.IsShortOptionOf('b'));
    }

    [Fact]
    public void KindSpecificAccessorsRejectOtherTokenKinds()
    {
        Token argument = Token.Parse("target");
        Token option = Token.Parse("--output");

        Assert.Throws<InvalidOperationException>(() => _ = argument.OptionName);
        Assert.Throws<InvalidOperationException>(() => _ = option.Argument);
        Assert.Throws<InvalidOperationException>(() => _ = option.ShortOptionNames);
    }

    [Fact]
    public void UnparsedFactoryPreservesRawToken()
    {
        Token token = Token.Unparsed("--literal");

        Assert.True(token.IsUnparsed);
        Assert.Equal("--literal", token.Raw);
    }
}

public sealed class TokenListTests
{
    [Fact]
    public void ConstructorMarksTokensAfterDoubleDashAsUnparsed()
    {
        var tokens = new TokenList(["build", "--", "--literal", "tail"]);
        Token[] allTokens = tokens.AllTokens.ToArray();

        Assert.Equal(
            [
                Token.TokenKind.Argument,
                Token.TokenKind.DoubleDash,
                Token.TokenKind.Unparsed,
                Token.TokenKind.Unparsed
            ],
            allTokens.Select(token => token.Kind));
    }

    [Fact]
    public void MatchAndRestAdvanceIndependentResultCollections()
    {
        var tokens = new TokenList(["matched", "rest", "unused"]);

        Token matched = tokens.Match();
        Token rest = tokens.Rest();

        Assert.Equal("matched", matched.Raw);
        Assert.Equal("rest", rest.Raw);
        Assert.Equal(["matched"], tokens.MatchedTokens.Select(token => token.Raw));
        Assert.Equal(["rest"], tokens.RestTokens.Select(token => token.Raw));
        Assert.Equal(["unused"], tokens.UnusedTokens.Select(token => token.Raw));
    }

    [Fact]
    public void ResetAllUnusedMovesRemainingTokensExceptDoubleDashToRest()
    {
        var tokens = new TokenList(["matched", "rest", "--", "tail"]);
        tokens.Match();
        tokens.Rest();

        tokens.ResetAllUnused();

        Assert.False(tokens.HasMore());
        Assert.Equal(["rest", "tail"], tokens.RestTokens.Select(token => token.Raw));
    }

    [Fact]
    public void ResetToHeadClearsProgressAndResultCollections()
    {
        var tokens = new TokenList(["first", "second"]);
        tokens.Match();
        tokens.Rest();

        tokens.ResetToHead();

        Assert.True(tokens.HasMore());
        Assert.Empty(tokens.MatchedTokens);
        Assert.Empty(tokens.RestTokens);
        Assert.Equal("first", tokens.Peek().Raw);
    }

    [Fact]
    public void EmptyListRejectsReadOperations()
    {
        var tokens = new TokenList(Array.Empty<string>());

        Assert.Throws<InvalidOperationException>(() => tokens.Peek());
        Assert.Throws<InvalidOperationException>(() => tokens.Match());
        Assert.Throws<InvalidOperationException>(() => tokens.Rest());
        Assert.Null(tokens.TryTakeArgument());
    }
}
