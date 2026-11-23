using System.Reflection;
using HalconDotNet;

namespace HObjectMemory.Lifetime.Tests;

[TestClass]
public sealed class HObjectHolderTests
{
    [TestMethod]
    public void NewHolder_IsActiveAndHasNoAnchors()
    {
        using HObjectHolder holder = new();

        Assert.IsFalse(holder.IsDisposed);
        Assert.AreEqual(0, holder.TrackedObjectCount);
    }

    [TestMethod]
    public void TrackAll_WithNullArguments_Throws()
    {
        using HObjectHolder holder = new();

        Assert.ThrowsExactly<ArgumentNullException>(() => holder.TrackAll(null!, "op"));
        Assert.ThrowsExactly<ArgumentNullException>(() => holder.TrackAll(Array.Empty<HObject>(), null!));
    }

    [TestMethod]
    public void TrackAll_WithEmptyArray_ReturnsEmptyArray()
    {
        using HObjectHolder holder = new();

        Assert.AreEqual(0, holder.TrackAll([], "op").Length);
        Assert.AreEqual(0, holder.TrackedObjectCount);
    }

    [TestMethod]
    public void TrackAll_WithNullElement_ThrowsAndTracksNothing()
    {
        using HObjectHolder holder = new();

        Assert.ThrowsExactly<InvalidOperationException>(() => holder.TrackAll([null!], "op"));
        Assert.AreEqual(0, holder.TrackedObjectCount);
    }

    [TestMethod]
    public void TrackAll_WithUninitializedObject_ThrowsAndTracksNothing()
    {
        TestHelpers.RequireHalconRuntime();
        using HObjectHolder holder = new();
        HObject uninitialized = new();

        Assert.ThrowsExactly<InvalidOperationException>(() => holder.TrackAll([uninitialized], "op"));
        Assert.AreEqual(0, holder.TrackedObjectCount);
        uninitialized.Dispose();
    }

    [TestMethod]
    public void TrackAll_AfterHolderDispose_ThrowsAndRollsBackOutput()
    {
        TestHelpers.RequireHalconRuntime();
        HObjectHolder holder = new();
        HObject raw = TestHelpers.CreateEmptyHObject();
        holder.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => holder.TrackAll([raw], "op"));
        Assert.IsFalse(raw.IsInitialized());
        Assert.AreEqual(0, holder.TrackedObjectCount);
    }

    [TestMethod]
    public void TrackAll_Success_RegistersAnchorsAndReleasesOnHolderDispose()
    {
        TestHelpers.RequireHalconRuntime();
        HObject first = TestHelpers.CreateEmptyHObject();
        HObject second = TestHelpers.CreateEmptyHObject();
        HObjectHolder holder = new();

        HObjectWeakHandle<HObject>[] weaks = holder.TrackAll([first, second], "op");

        Assert.AreEqual(2, weaks.Length);
        Assert.AreEqual(2, holder.TrackedObjectCount);
        Assert.IsTrue(weaks.All(weak => weak.IsAlive));
        Assert.AreEqual(1, holder.GetStrongRefCount(weaks[0]));
        Assert.AreEqual(1, holder.GetStrongRefCount(weaks[1]));

        holder.Dispose();
        Assert.IsFalse(first.IsInitialized());
        Assert.IsFalse(second.IsInitialized());
        Assert.IsTrue(weaks.All(weak => !weak.IsAlive));
    }

    [TestMethod]
    public void BorrowAndGetStrongRefCount_CoverEmptyNullAndPromotedStates()
    {
        TestHelpers.RequireHalconRuntime();
        using HObjectHolder holder = new();
        HObject raw = TestHelpers.CreateEmptyHObject();
        HObjectWeakHandle<HObject> weak = holder.TrackAll([raw], "op")[0];

        Assert.ThrowsExactly<ArgumentNullException>(() => holder.GetStrongRefCount(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => holder.TryBorrow(null!, out _));
        Assert.AreEqual(0, holder.GetStrongRefCount(HObjectWeakHandle<HObject>.Empty));
        Assert.AreEqual(1, holder.GetStrongRefCount(weak));

        Assert.IsTrue(holder.TryBorrow(weak, out HObjectHandle<HObject> strong));
        Assert.IsTrue(strong.Value.IsInitialized());
        Assert.AreEqual(2, holder.GetStrongRefCount(weak));

        strong.Dispose();
        Assert.AreEqual(1, holder.GetStrongRefCount(weak));
    }

    [TestMethod]
    public void Borrow_WhenWeakExpired_Throws()
    {
        TestHelpers.RequireHalconRuntime();
        HObjectHolder holder = new();
        HObject raw = TestHelpers.CreateEmptyHObject();
        HObjectWeakHandle<HObject> weak = holder.TrackAll([raw], "op")[0];
        holder.Dispose();

        Assert.IsFalse(holder.TryBorrow(weak, out HObjectHandle<HObject> expired));
        Assert.IsTrue(expired.IsEmpty);
        Assert.ThrowsExactly<ObjectDisposedException>(() => holder.Borrow(weak));
    }

    [TestMethod]
    public void ExternallyDisposedHObject_InvalidatesWeakAndBlocksPromotion()
    {
        TestHelpers.RequireHalconRuntime();
        using HObjectHolder holder = new();
        HObject raw = TestHelpers.CreateEmptyHObject();
        HObjectWeakHandle<HObject> weak = holder.TrackAll([raw], "op")[0];

        raw.Dispose();

        Assert.IsFalse(weak.IsAlive);
        Assert.IsFalse(holder.TryBorrow(weak, out HObjectHandle<HObject> promoted));
        Assert.IsTrue(promoted.IsEmpty);
    }

    [TestMethod]
    public void Release_RemovesOneAnchorAndIsIdempotent()
    {
        TestHelpers.RequireHalconRuntime();
        using HObjectHolder holder = new();
        HObject raw = TestHelpers.CreateEmptyHObject();
        HObjectWeakHandle<HObject> weak = holder.TrackAll([raw], "op")[0];

        Assert.ThrowsExactly<ArgumentNullException>(() => holder.Release(null!));
        Assert.IsFalse(holder.Release(HObjectWeakHandle<HObject>.Empty));

        Assert.IsTrue(holder.Release(weak));
        Assert.IsFalse(raw.IsInitialized());
        Assert.IsFalse(weak.IsAlive);
        Assert.AreEqual(0, holder.TrackedObjectCount);
        Assert.IsFalse(holder.Release(weak));
    }

    [TestMethod]
    public void Release_WithWeakFromAnotherHolder_ReturnsFalse()
    {
        TestHelpers.RequireHalconRuntime();
        using HObjectHolder first = new();
        using HObjectHolder second = new();
        HObject firstRaw = TestHelpers.CreateEmptyHObject();
        HObject secondRaw = TestHelpers.CreateEmptyHObject();
        HObjectWeakHandle<HObject> firstWeak = first.TrackAll([firstRaw], "op")[0];
        HObjectWeakHandle<HObject> otherWeak = second.TrackAll([secondRaw], "op")[0];

        Assert.IsFalse(first.Release(otherWeak));
        Assert.AreEqual(1, first.TrackedObjectCount);
        Assert.AreEqual(1, second.TrackedObjectCount);
        Assert.IsTrue(firstWeak.IsAlive);
        Assert.IsTrue(otherWeak.IsAlive);
    }

    [TestMethod]
    public void Release_WithPromotedStrongRef_DefersUnderlyingDispose()
    {
        TestHelpers.RequireHalconRuntime();
        using HObjectHolder holder = new();
        HObject raw = TestHelpers.CreateEmptyHObject();
        HObjectWeakHandle<HObject> weak = holder.TrackAll([raw], "op")[0];
        HObjectHandle<HObject> strong = holder.Borrow(weak);

        Assert.IsTrue(holder.Release(weak));
        Assert.IsTrue(raw.IsInitialized());
        Assert.AreEqual(1, holder.GetStrongRefCount(weak));

        strong.Dispose();
        Assert.IsFalse(raw.IsInitialized());
    }

    [TestMethod]
    public void Dispose_IsIdempotentAndClearsAllAnchors()
    {
        TestHelpers.RequireHalconRuntime();
        HObjectHolder holder = new();
        HObject raw = TestHelpers.CreateEmptyHObject();
        HObjectWeakHandle<HObject> weak = holder.TrackAll([raw], "op")[0];

        holder.Dispose();
        holder.Dispose();

        Assert.IsTrue(holder.IsDisposed);
        Assert.AreEqual(0, holder.TrackedObjectCount);
        Assert.IsFalse(weak.IsAlive);
        Assert.IsFalse(raw.IsInitialized());
    }

    [TestMethod]
    public void Finalizer_ReleasesAnchorsWhenDisposeWasNotCalled()
    {
        TestHelpers.RequireHalconRuntime();
        HObjectHolder holder = new();
        HObject raw = TestHelpers.CreateEmptyHObject();
        HObjectWeakHandle<HObject> weak = holder.TrackAll([raw], "op")[0];

        typeof(HObjectHolder)
            .GetMethod("Finalize", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(holder, null);
        GC.SuppressFinalize(holder);

        Assert.IsTrue(holder.IsDisposed);
        Assert.AreEqual(0, holder.TrackedObjectCount);
        Assert.IsFalse(weak.IsAlive);
        Assert.IsFalse(raw.IsInitialized());
    }

    [TestMethod]
    public void DisposeRawOutputs_DisposesInitializedOnlyAndIgnoresNullOrUninitialized()
    {
        TestHelpers.RequireHalconRuntime();
        HObject initialized = TestHelpers.CreateEmptyHObject();
        HObject uninitialized = new();

        HObjectHolder.DisposeRawOutputs([null!, uninitialized, initialized]);

        Assert.IsFalse(initialized.IsInitialized());
        Assert.IsFalse(uninitialized.IsInitialized());
    }

    [TestMethod]
    public void TrackAll_ConcurrentCalls_RegisterEveryAnchor()
    {
        TestHelpers.RequireHalconRuntime();
        using HObjectHolder holder = new();
        HObject[] rawObjects = Enumerable.Range(0, 32).Select(_ => TestHelpers.CreateEmptyHObject()).ToArray();
        HObjectWeakHandle<HObject>[] weaks = new HObjectWeakHandle<HObject>[rawObjects.Length];

        Parallel.For(0, rawObjects.Length, i => weaks[i] = holder.TrackAll([rawObjects[i]], "op")[0]);

        Assert.AreEqual(rawObjects.Length, holder.TrackedObjectCount);
        Assert.IsTrue(weaks.All(weak => weak.IsAlive));
        holder.Dispose();
        Assert.IsTrue(rawObjects.All(raw => !raw.IsInitialized()));
    }
}
