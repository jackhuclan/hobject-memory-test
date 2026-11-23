namespace HObjectMemory.Lifetime.Tests;

[TestClass]
public sealed class HObjectHandleTests
{
    [TestMethod]
    public void FromNull_CreatesEmptyHandleWithSafeOperations()
    {
        HObjectHandle<FakeResource> handle = HObjectHandle<FakeResource>.From(null!);

        Assert.IsTrue(handle.IsEmpty);
        Assert.AreEqual(0, handle.GetHashCode());
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = handle.Value);
        Assert.IsFalse(handle.TryGetValue(out FakeResource? value));
        Assert.IsNull(value);

        HObjectHandle<FakeResource> shared = handle.Share();
        Assert.IsTrue(shared.IsEmpty);
        Assert.IsTrue(handle.Move().IsEmpty);
        Assert.IsFalse(handle.AsWeak().IsAlive);

        handle.Dispose();
        handle.Dispose();
        Assert.IsTrue(handle.IsEmpty);
    }

    [TestMethod]
    public void EmptyStaticInstance_IsEmptyAndEqualsItselfOnly()
    {
        HObjectHandle<FakeResource> empty = HObjectHandle<FakeResource>.Empty;

        HObjectHandle<FakeResource> anotherEmpty = HObjectHandle<FakeResource>.From(null!);

        Assert.IsTrue(empty.IsEmpty);
        Assert.IsTrue(empty.Equals(empty));
        Assert.IsTrue(empty.Equals(HObjectHandle<FakeResource>.Empty)); // 静态单例，同一引用
        Assert.IsFalse(empty.Equals(anotherEmpty));                    // 不同空句柄不相等
        Assert.IsFalse(empty.Equals((object)anotherEmpty));
        Assert.IsFalse(empty.Equals((object?)null));
        Assert.IsFalse(empty.Equals("not a handle"));
    }

    [TestMethod]
    public void FromValue_ExposesValueAndOwnerHashCode()
    {
        FakeResource resource = new();
        HObjectHandle<FakeResource> handle = HObjectHandle<FakeResource>.From(resource);

        Assert.IsFalse(handle.IsEmpty);
        Assert.AreSame(resource, handle.Value);
        Assert.IsTrue(handle.TryGetValue(out FakeResource? value));
        Assert.AreSame(resource, value);
        Assert.AreEqual(handle.Owner!.GetHashCode(), handle.GetHashCode());
    }

    [TestMethod]
    public void Share_CreatesAdditionalStrongRefAndDisposesOnlyAtLastRelease()
    {
        FakeResource resource = new();
        HObjectHandle<FakeResource> first = HObjectHandle<FakeResource>.From(resource);
        HObjectHandle<FakeResource> second = first.Share();
        HObjectHandle<FakeResource> third = second.Share();

        Assert.AreEqual(3, first.Owner!.StrongCount);
        Assert.IsTrue(first.Equals(second));
        Assert.AreEqual(first.GetHashCode(), third.GetHashCode());

        first.Dispose();
        second.Dispose();
        Assert.AreEqual(0, resource.DisposeCount);
        Assert.IsFalse(third.IsEmpty);

        third.Dispose();
        Assert.AreEqual(1, resource.DisposeCount);
        Assert.IsTrue(third.IsEmpty);
    }

    [TestMethod]
    public void Share_AfterHandleDispose_Throws()
    {
        HObjectHandle<FakeResource> handle = HObjectHandle<FakeResource>.From(new FakeResource());
        handle.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => handle.Share());
    }

    [TestMethod]
    public void Share_WhenOwnerAlreadyInvalid_Throws()
    {
        HObjectHandle<FakeResource> handle = new(new FakeResource(), _ => false);

        Assert.IsTrue(handle.IsEmpty);
        Assert.ThrowsExactly<ObjectDisposedException>(() => handle.Share());
    }

    [TestMethod]
    public void Move_TransfersOwnershipAndEmptiesSource()
    {
        FakeResource resource = new();
        HObjectHandle<FakeResource> source = HObjectHandle<FakeResource>.From(resource);

        HObjectHandle<FakeResource> moved = source.Move();

        Assert.IsTrue(source.IsEmpty);
        Assert.IsFalse(moved.IsEmpty);
        Assert.AreSame(resource, moved.Value);
        Assert.AreEqual(1, moved.Owner!.StrongCount);

        source.Dispose();
        Assert.AreEqual(0, resource.DisposeCount);

        moved.Dispose();
        Assert.AreEqual(1, resource.DisposeCount);
    }

    [TestMethod]
    public void Move_WhenOwnerAlreadyInvalid_ReturnsEmpty()
    {
        FakeResource resource = new();
        HObjectHandle<FakeResource> handle = new(resource, _ => false);

        HObjectHandle<FakeResource> moved = handle.Move();

        Assert.IsTrue(handle.IsEmpty);
        Assert.IsTrue(moved.IsEmpty);
        Assert.AreEqual(0, resource.DisposeCount);
    }

    [TestMethod]
    public void ValueAndTryGetValue_AfterDispose_ReportReleased()
    {
        FakeResource resource = new();
        HObjectHandle<FakeResource> handle = HObjectHandle<FakeResource>.From(resource);
        handle.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = handle.Value);
        Assert.IsFalse(handle.TryGetValue(out FakeResource? value));
        Assert.IsNull(value);
        Assert.AreEqual(1, resource.DisposeCount);
    }

    [TestMethod]
    public void AsWeak_PromotesWhileAliveAndFailsAfterAllStrongRefsReleased()
    {
        FakeResource resource = new();
        HObjectHandle<FakeResource> owner = HObjectHandle<FakeResource>.From(resource);
        HObjectWeakHandle<FakeResource> weak = owner.AsWeak();

        Assert.IsTrue(weak.IsAlive);
        Assert.IsTrue(weak.TryGetTarget(out HObjectHandle<FakeResource> promoted));
        Assert.AreSame(resource, promoted.Value);

        owner.Dispose();
        Assert.AreEqual(0, resource.DisposeCount);
        Assert.IsTrue(weak.IsAlive);

        promoted.Dispose();
        Assert.AreEqual(1, resource.DisposeCount);
        Assert.IsFalse(weak.IsAlive);
        Assert.IsFalse(weak.TryGetTarget(out HObjectHandle<FakeResource> expired));
        Assert.IsTrue(expired.IsEmpty);
        weak.Dispose();
    }

    [TestMethod]
    public void AsWeak_AfterHandleDisposeOrInvalidOwner_ReturnsEmpty()
    {
        HObjectHandle<FakeResource> disposed = HObjectHandle<FakeResource>.From(new FakeResource());
        disposed.Dispose();
        HObjectHandle<FakeResource> invalid = new(new FakeResource(), _ => false);

        Assert.IsFalse(disposed.AsWeak().IsAlive);
        Assert.IsFalse(invalid.AsWeak().IsAlive);
    }

    [TestMethod]
    public void ConcurrentDispose_ReleasesResourceExactlyOnce()
    {
        FakeResource resource = new();
        HObjectHandle<FakeResource> root = HObjectHandle<FakeResource>.From(resource);
        HObjectHandle<FakeResource>[] handles = Enumerable.Range(0, 32).Select(_ => root.Share()).ToArray();

        root.Dispose();
        Parallel.ForEach(handles, handle => handle.Dispose());

        Assert.AreEqual(1, resource.DisposeCount);
        Assert.IsTrue(handles.All(handle => handle.IsEmpty));
    }

    [TestMethod]
    public void Equals_CoversReferenceNullOwnerSameOwnerAndDifferentOwnerBranches()
    {
        FakeResource firstResource = new();
        FakeResource secondResource = new();
        HObjectHandle<FakeResource> first = HObjectHandle<FakeResource>.From(firstResource);
        HObjectHandle<FakeResource> sameOwner = first.Share();
        HObjectHandle<FakeResource> differentOwner = HObjectHandle<FakeResource>.From(secondResource);

        Assert.IsTrue(first.Equals(first));
        Assert.IsTrue(first.Equals(sameOwner));
        Assert.IsFalse(first.Equals((HObjectHandle<FakeResource>?)null));
        Assert.IsFalse(first.Equals(differentOwner));
        Assert.IsTrue(first.Equals((object)sameOwner));
        Assert.IsFalse(first.Equals((object?)null));

        first.Dispose();
        Assert.IsFalse(first.Equals(sameOwner));

        sameOwner.Dispose();
        differentOwner.Dispose();
    }
}
