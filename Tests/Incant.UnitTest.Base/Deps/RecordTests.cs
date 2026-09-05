using Record = Incant.Base.Deps.Record;

namespace Incant.UnitTest.Base.Deps;

public sealed class RecordTests
{
    [Fact]
    public void ExternalFileAddMethodsRejectInvalidPaths()
    {
        var record = new Record();

        Assert.Throws<ArgumentNullException>(() => record.AddExternalFile(null!));
        Assert.Throws<ArgumentException>(() => record.AddExternalFile(string.Empty));
        Assert.Throws<ArgumentNullException>(() => record.AddExternalFileRange(null!));
        Assert.Throws<ArgumentNullException>(() => record.AddExternalFileRange([null!]));
        Assert.Throws<ArgumentException>(() => record.AddExternalFileRange([string.Empty]));
    }
}
