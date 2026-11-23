namespace HObjectMemory.Lifetime.Tests;

[TestClass]
public sealed class HObjectWeakHandleTests
{
    [TestMethod]
    public void Empty_IsDeadAndCannotPromote()
    {
        HObjectWeakHandle<FakeResource> weak = HObjectWeakHandle<FakeResource>.Empty;

        Assert.IsFalse(weak.IsAlive);
        Assert.IsFalse(weak.TryGetTarget(out HObjectHandle<FakeResource> target));
        Assert.IsTrue(target.IsEmpty);
        weak.Dispose();
    }

    [TestMethod]
    public void IsAlive_ReflectsOwnerStateAndDisposeOfObserver()
    {
        FakeResource resource = new();
        HObjectHandle<FakeResource> strong = HObjectHandle<FakeResource>.From(resource);
        HObjectWeakHandle<FakeResource> weak = strong.AsWeak();

        Assert.IsTrue(weak.IsAlive);

        weak.Dispose();
        Assert.IsFalse(weak.IsAlive);
        Assert.IsFalse(weak.TryGetTarget(out _));
        Assert.AreEqual(0, resource.DisposeCount);
        Assert.AreSame(resource, strong.Value);

        strong.Dispose();
        Assert.AreEqual(1, resource.DisposeCount);
    }

    [TestMethod]
    public void TryGetTarget_PromotesMultipleTimesAndDoesNotExtendLifetime()
    {
        FakeResource resource = new();
        HObjectHandle<FakeResource> owner = HObjectHandle<FakeResource>.From(resource);
        HObjectWeakHandle<FakeResource> weak = owner.AsWeak();

        Assert.IsTrue(weak.TryGetTarget(out HObjectHandle<FakeResource> first));
        Assert.IsTrue(weak.TryGetTarget(out HObjectHandle<FakeResource> second));
        Assert.AreEqual(3, owner.Owner!.StrongCount);
        Assert.AreSame(resource, first.Value);
        Assert.AreSame(resource, second.Value);

        owner.Dispose();
        first.Dispose();
        Assert.IsTrue(weak.IsAlive);
        Assert.AreEqual(0, resource.DisposeCount);

        second.Dispose();
        Assert.IsFalse(weak.IsAlive);
        Assert.AreEqual(1, resource.DisposeCount);
        Assert.IsFalse(weak.TryGetTarget(out HObjectHandle<FakeResource> expired));
        Assert.IsTrue(expired.IsEmpty);
    }

    [TestMethod]
    public void Dispose_IsIdempotentForObserverWithOwner()
    {
        FakeResource resource = new();
        HObjectHandle<FakeResource> owner = HObjectHandle<FakeResource>.From(resource);
        HObjectWeakHandle<FakeResource> weak = owner.AsWeak();

        weak.Dispose();
        weak.Dispose();
        weak.Dispose();

        Assert.AreEqual(1, owner.Owner!.StrongCount);
        Assert.AreEqual(0, resource.DisposeCount);
        Assert.IsFalse(weak.IsAlive);

        owner.Dispose();
        Assert.AreEqual(1, resource.DisposeCount);
    }
}
