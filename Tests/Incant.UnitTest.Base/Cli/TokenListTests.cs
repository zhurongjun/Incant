using Incant.Base.Cli;

namespace Incant.UnitTest.Base.Cli;

public sealed class TokenListTests
{
    [Fact]
    public void TokenConstructorPreservesInstancesAndOrder()
    {
        Token first = Token.Parse("first");
        Token second = Token.Unparsed("--literal");
        var tokens = new TokenList([first, second]);

        Assert.Equal([first, second], tokens.AllTokens);
        Assert.Same(first, tokens.Peek());
    }

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
    public void EveryTokenAfterFirstDoubleDashRemainsUnparsed()
    {
        var tokens = new TokenList(["--", "--", "-x", "tail"]);

        Assert.Equal(
            [
                Token.TokenKind.DoubleDash,
                Token.TokenKind.Unparsed,
                Token.TokenKind.Unparsed,
                Token.TokenKind.Unparsed
            ],
            tokens.AllTokens.Select(token => token.Kind));
    }

    [Fact]
    public void PeekDoesNotConsumeTheNextToken()
    {
        var tokens = new TokenList(["first", "second"]);

        Token firstPeek = tokens.Peek();
        Token secondPeek = tokens.Peek();

        Assert.Same(firstPeek, secondPeek);
        Assert.Equal(["first", "second"], tokens.UnusedTokens.Select(token => token.Raw));
    }

    [Fact]
    public void TryTakeArgumentConsumesOnlyPositionalArguments()
    {
        var arguments = new TokenList(["value", "--option"]);

        Token? argument = arguments.TryTakeArgument();
        Token? option = arguments.TryTakeArgument();

        Assert.NotNull(argument);
        Assert.Equal("value", argument.Raw);
        Assert.Null(option);
        Assert.Equal("--option", arguments.Peek().Raw);
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

        tokens.ResetAllUnused();

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
        Assert.False(tokens.HasMore());
        Assert.Empty(tokens.AllTokens);
        Assert.Empty(tokens.MatchedTokens);
        Assert.Empty(tokens.RestTokens);
        Assert.Empty(tokens.UnusedTokens);
    }
}

