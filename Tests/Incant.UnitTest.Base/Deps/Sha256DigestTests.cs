using Incant.Base.Deps;

namespace Incant.UnitTest.Base.Deps;

public sealed class Sha256DigestTests
{
    [Fact]
    public void BytesAndHexRoundTripWithoutChangingBytesOutsideTheDestination()
    {
        byte[] bytes = Enumerable.Range(0, Sha256Digest.SizeInBytes).Select(static value => (byte)value).ToArray();
        const string Hex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
        Sha256Digest digest = Sha256Digest.FromBytes(bytes);
        Assert.Equal(Hex, digest.ToString());
        Assert.Equal(digest, Sha256Digest.Parse(Hex.ToUpperInvariant()));

        byte[] destination = Enumerable.Repeat((byte)0xFF, Sha256Digest.SizeInBytes + 2).ToArray();
        digest.CopyTo(destination.AsSpan(1));
        Assert.Equal(bytes, destination[1..^1]);
        Assert.Equal(0xFF, destination[0]);
        Assert.Equal(0xFF, destination[^1]);
        Assert.NotEqual(digest, default);
        Assert.Equal(new byte[Sha256Digest.SizeInBytes], Convert.FromHexString(default(Sha256Digest).ToString()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void FromBytesRejectsEveryNonDigestLength(int length)
    {
        Assert.Throws<ArgumentException>(() => Sha256Digest.FromBytes(new byte[length]));
    }

    [Fact]
    public void CopyToRejectsShortDestinationWithoutWriting()
    {
        byte[] destination = Enumerable.Repeat((byte)0xFF, Sha256Digest.SizeInBytes - 1).ToArray();
        Assert.Throws<ArgumentException>(() => default(Sha256Digest).CopyTo(destination));
        Assert.All(destination, static value => Assert.Equal(0xFF, value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1")]
    [InlineData("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f0")]
    [InlineData("g00102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f")]
    public void ParseRejectsMalformedHex(string text)
    {
        Assert.Throws<FormatException>(() => Sha256Digest.Parse(text));
    }
}
