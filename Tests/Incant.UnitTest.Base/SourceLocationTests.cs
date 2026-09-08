using Incant.Base;

namespace Incant.UnitTest.Base;

public sealed class SourceLocationTests
{
    [Fact]
    public void CallerFileAndDirectoryIdentifyTheCallingSource()
    {
        string file = SourceLocation.File();
        string directory = SourceLocation.Directory();

        Assert.Equal(nameof(SourceLocationTests) + ".cs", Path.GetFileName(file));
        Assert.Equal(Path.GetDirectoryName(file), directory);
    }

    [Fact]
    public void CallerLineRespectsCompilerLineMapping()
    {
#line 1200
        int line = SourceLocation.Line();
#line default

        Assert.Equal(1200, line);
    }

    [Fact]
    public void CallerMemberIdentifiesMethodsAndProperties()
    {
        Assert.Equal(nameof(CallerMemberIdentifiesMethodsAndProperties), SourceLocation.MemberName());
        Assert.Equal(nameof(CallingProperty), CallingProperty);
    }

    [Fact]
    public void ExplicitPathsRemainLexicalWithoutRequiringAnExistingFile()
    {
        string path = Path.Combine("virtual source", "nested", "..", "Build.cs");

        Assert.Equal(path, SourceLocation.File(path));
        Assert.Equal(Path.Combine("virtual source", "nested", ".."), SourceLocation.Directory(path));
    }

    [Fact]
    public void ExplicitOverridesAndMissingInformationRemainDistinct()
    {
        Assert.Equal("external member", SourceLocation.MemberName("external member"));
        Assert.Equal(42, SourceLocation.Line(42));
        Assert.Equal(0, SourceLocation.Line(0));
        Assert.Empty(SourceLocation.File(""));
        Assert.Empty(SourceLocation.Directory(""));
        Assert.Empty(SourceLocation.Directory("Build.cs"));
        Assert.Empty(SourceLocation.MemberName(""));
    }

    [Fact]
    public void InvalidOverridesReportTheRelevantParameter()
    {
        Assert.Equal("path", Assert.Throws<ArgumentNullException>(() => SourceLocation.File(null!)).ParamName);
        Assert.Equal("path", Assert.Throws<ArgumentNullException>(() => SourceLocation.Directory(null!)).ParamName);
        Assert.Equal("member", Assert.Throws<ArgumentNullException>(() => SourceLocation.MemberName(null!)).ParamName);
        Assert.Equal("line", Assert.Throws<ArgumentOutOfRangeException>(() => SourceLocation.Line(-1)).ParamName);
    }

    private static string CallingProperty => SourceLocation.MemberName();
}
