using System.Reflection;

namespace HObjectMemory.Lifetime.Tests;

[TestClass]
public sealed class RefCountedOwnerTests
{
    [TestMethod]
    public void Constructor_InitializesOneStrongAndOneWeakReference()
    {
        FakeResource resource = new();
        RefCountedOwner<FakeResource> owner = new(resource);

        Assert.AreEqual(1, owner.StrongCount);
        Assert.AreEqual(1, GetPrivateValue(owner, "_weakCount"));
        Assert.IsFalse(owner.IsDisposed);
        Assert.AreSame(resource, GetPrivateValue(owner, "_value"));
    }

    [TestMethod]
    public void ValueAndTryGetValue_WhenAlive_ReturnTheSameResource()
    {
        FakeResource resource = new();
        RefCountedOwner<FakeResource> owner = new(resource, _ => true);

        Assert.AreSame(resource, owner.Value);
        Assert.IsTrue(owner.TryGetValue(out FakeResource? value));
        Assert.AreSame(resource, value);
    }

    [TestMethod]
    public void ValueAndTryGetValue_AfterLastStrongRelease_ReportDisposed()
    {
        FakeResource resource = new();
        RefCountedOwner<FakeResource> owner = new(resource);
        owner.ReleaseStrong();

        Assert.IsTrue(owner.IsDisposed);
        Assert.AreEqual(0, owner.StrongCount);
        Assert.AreEqual(1, resource.DisposeCount);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = owner.Value);
        Assert.IsFalse(owner.TryGetValue(out FakeResource? value));
        Assert.IsNull(value);
    }

    [TestMethod]
    public void AliveProbeReturningFalse_MakesOwnerAppearDisposedWithoutDisposingResource()
    {
        FakeResource resource = new();
        RefCountedOwner<FakeResource> owner = new(resource, _ => false);

        Assert.IsTrue(owner.IsDisposed);
        Assert.AreEqual(1, owner.StrongCount);
        Assert.AreEqual(0, resource.DisposeCount);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = owner.Value);
        Assert.IsFalse(owner.TryGetValue(out FakeResource? value));
        Assert.IsNull(value);
        Assert.IsFalse(owner.TryAddStrongRef());
    }

    [TestMethod]
    public void AliveProbeThrowing_IsTreatedAsDisposed()
    {
        FakeResource resource = new();
        RefCountedOwner<FakeResource> owner = new(resource, _ => throw new InvalidOperationException("probe failed"));

        Assert.IsTrue(owner.IsDisposed);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = owner.Value);
        Assert.IsFalse(owner.TryGetValue(out _));
        Assert.IsFalse(owner.TryAddStrongRef());
    }

    [TestMethod]
    public void TryAddStrongRef_WhenProbeFlipsAfterCas_RollsBackAndReturnsFalse()
    {
        FakeResource resource = new();
        int probeCalls = 0;
        RefCountedOwner<FakeResource> owner = new(resource, _ => ++probeCalls == 1);

        bool added = owner.TryAddStrongRef();

        Assert.IsFalse(added);
        Assert.AreEqual(2, probeCalls);
        Assert.AreEqual(1, owner.StrongCount);
        Assert.AreEqual(0, resource.DisposeCount);
    }

    [TestMethod]
    public void TryAddStrongRef_WhenStrongCountIsZero_ReturnsFalse()
    {
        FakeResource resource = new();
        RefCountedOwner<FakeResource> owner = new(resource, _ => true);
        SetPrivateValue(owner, "_strongCount", 0);

        Assert.IsFalse(owner.TryAddStrongRef());
        Assert.IsFalse(owner.IsDisposed);
        Assert.AreSame(resource, owner.Value);
    }

    [TestMethod]
    public void TryAddStrongRef_ConcurrentCallers_AllSucceed()
    {
        FakeResource resource = new();
        RefCountedOwner<FakeResource> owner = new(resource, _ => true);
        bool[] results = new bool[64];

        Parallel.For(0, results.Length, i => results[i] = owner.TryAddStrongRef());

        Assert.IsTrue(results.All(x => x));
        Assert.AreEqual(65, owner.StrongCount);
        Assert.AreEqual(0, resource.DisposeCount);

        for (int i = 0; i < results.Length; i++)
        {
            owner.ReleaseStrong();
        }

        Assert.AreEqual(1, owner.StrongCount);
        owner.ReleaseStrong();
        Assert.AreEqual(0, owner.StrongCount);
        Assert.AreEqual(1, resource.DisposeCount);
    }

    [TestMethod]
    public void ReleaseStrong_WithSharedRefs_DisposesOnlyWhenLastReleased()
    {
        FakeResource resource = new();
        RefCountedOwner<FakeResource> owner = new(resource);
        Assert.IsTrue(owner.TryAddStrongRef());

        owner.ReleaseStrong();
        Assert.AreEqual(1, owner.StrongCount);
        Assert.AreEqual(0, resource.DisposeCount);
        Assert.IsFalse(owner.IsDisposed);

        owner.ReleaseStrong();
        Assert.AreEqual(0, owner.StrongCount);
        Assert.AreEqual(1, resource.DisposeCount);
        Assert.IsTrue(owner.IsDisposed);
    }

    [TestMethod]
    public void ReleaseStrong_WhenValueAlreadyNull_StillReleasesWeakCount()
    {
        RefCountedOwner<FakeResource> owner = new(new FakeResource());
        SetPrivateValue(owner, "_value", null);

        owner.ReleaseStrong();

        Assert.AreEqual(0, owner.StrongCount);
        Assert.AreEqual(0, GetPrivateValue(owner, "_weakCount"));
    }

    [TestMethod]
    public void AddWeakRefAndReleaseWeak_ChangeOnlyWeakCount()
    {
        RefCountedOwner<FakeResource> owner = new(new FakeResource());

        owner.AddWeakRef();
        Assert.AreEqual(2, GetPrivateValue(owner, "_weakCount"));
        Assert.AreEqual(1, owner.StrongCount);

        owner.ReleaseWeak();
        Assert.AreEqual(1, GetPrivateValue(owner, "_weakCount"));
        Assert.AreEqual(1, owner.StrongCount);
    }

    private static object? GetPrivateValue(object target, string fieldName)
    {
        return target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target);
    }

    private static void SetPrivateValue(object target, string fieldName, object? value)
    {
        target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }
}
