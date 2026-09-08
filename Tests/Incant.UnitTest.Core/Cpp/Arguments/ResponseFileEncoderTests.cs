using System.Text;
using Incant.Core.Cpp.Arguments;

namespace Incant.UnitTest.Core.Cpp.Arguments;

public sealed class ResponseFileEncoderTests
{
    [Fact]
    public void MicrosoftEncodingPreservesEmptyUnicodeQuoteAndTrailingBackslashBoundaries()
    {
        byte[] bytes = ResponseFileEncoder.Encode(["", "路径 with space\\", "a\"b"], ResponseFileDialect.Msvc);
        Assert.Equal([0xff, 0xfe], bytes.Take(2).Select(value => (int)value));
        string content = Encoding.Unicode.GetString(bytes.AsSpan(2));
        Assert.Equal("\"\"\n\"路径 with space\\\\\"\n\"a\\\"b\"\n", content);
    }

    [Fact]
    public void GnuAndLlvmWindowsHaveDifferentBackslashRules()
    {
        const string Argument = "a\\b";
        string gnu = Encoding.UTF8.GetString(ResponseFileEncoder.Encode([Argument], ResponseFileDialect.Gnu));
        string windows = Encoding.UTF8.GetString(ResponseFileEncoder.Encode([Argument], ResponseFileDialect.LlvmWindows));
        Assert.Equal("\"a\\\\b\"\n", gnu);
        Assert.Equal("\"a\\b\"\n", windows);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    [InlineData("@nested.rsp")]
    public void UnrepresentableOrRecursiveTransportIsRejected(string argument)
    {
        Assert.Throws<ArgumentException>(() => ResponseFileEncoder.Encode([argument], ResponseFileDialect.Msvc));
        Assert.Throws<ArgumentException>(() => ResponseFileEncoder.Encode([argument], ResponseFileDialect.Gnu));
    }
}
