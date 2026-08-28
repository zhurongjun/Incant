using Incant.Base.Cli;

namespace Incant.UnitTests.Cli;

public sealed class ParseContextTests
{
    [Fact]
    public void NewContextHasNoDiagnosticsSnapshotOrVisitedOptions()
    {
        var context = new ParseContext
        {
            Tokens = new TokenList(Array.Empty<string>())
        };

        Assert.False(context.AnyError);
        Assert.False(context.AnyWarning);
        Assert.Equal(0, context.ErrorCount);
        Assert.Equal(0, context.WarningCount);
        Assert.Null(context.CommandTokens);
        Assert.Empty(context.VisitedOptions);
        Assert.True(context.Writer.IsEmpty);
    }

    [Fact]
    public void DiagnosticsSetFlagsAndAppendStyledMessages()
    {
        var context = new ParseContext
        {
            Tokens = new TokenList(Array.Empty<string>())
        };

        context.Warning("warning message");
        context.Error("error message");

        Assert.True(context.AnyWarning);
        Assert.True(context.AnyError);
        Assert.Equal(1, context.WarningCount);
        Assert.Equal(1, context.ErrorCount);
        Assert.Contains("Warning:", context.Writer.Content);
        Assert.Contains("warning message", context.Writer.Content);
        Assert.Contains("Error:", context.Writer.Content);
        Assert.Contains("error message", context.Writer.Content);
    }

    [Fact]
    public void StoreCommandTokensSnapshotsUnusedTokensOnlyOnce()
    {
        var context = new ParseContext
        {
            Tokens = new TokenList(["matched", "unused"])
        };
        context.Tokens.Match();

        context.StoreCommandTokens();
        context.Tokens.Match();
        context.StoreCommandTokens();

        Assert.NotNull(context.CommandTokens);
        Assert.Equal(["unused"], context.CommandTokens.AllTokens.Select(token => token.Raw));
    }

    [Fact]
    public void StoreCommandTokensCanSnapshotAnEmptyRemainder()
    {
        var context = new ParseContext
        {
            Tokens = new TokenList(["matched"])
        };
        context.Tokens.Match();

        context.StoreCommandTokens();

        Assert.NotNull(context.CommandTokens);
        Assert.Empty(context.CommandTokens.AllTokens);
    }

    [Fact]
    public void DiagnosticsUseConfiguredWriter()
    {
        var writer = new Writer();
        var context = new ParseContext
        {
            Tokens = new TokenList(Array.Empty<string>()),
            Writer = writer
        };

        context.Error("failure");

        Assert.Same(writer, context.Writer);
        Assert.Contains("failure", writer.Content);
    }
}
