namespace Incant.UnitTests;

public sealed class TestInfrastructureSmokeTests
{
    [Fact]
    public void TestAssemblyHasExpectedName()
    {
        string? assemblyName = typeof(TestInfrastructureSmokeTests).Assembly.GetName().Name;

        Assert.Equal("Incant.UnitTests", assemblyName);
    }
}
