namespace HObjectMemory.Lifetime.Tests;

[TestClass]
public sealed class FakeResourceTests
{
    [TestMethod]
    public void NewResource_HasZeroDisposeCount()
    {
        FakeResource resource = new();

        Assert.AreEqual(0, resource.DisposeCount);
    }

    [TestMethod]
    public void Dispose_IncrementsCountAndIsIdempotentPerCall()
    {
        FakeResource resource = new();

        resource.Dispose();
        Assert.AreEqual(1, resource.DisposeCount);

        resource.Dispose();
        resource.Dispose();
        Assert.AreEqual(3, resource.DisposeCount);
    }
}
