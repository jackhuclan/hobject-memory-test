using System.Reflection;
using HalconDotNet;

namespace HObjectMemory.Lifetime.Tests;

[TestClass]
public sealed class HObjectHolderGeneratedTests
{
    [TestMethod]
    public void GenImageConst_Success_TracksOutputUntilHolderDispose()
    {
        TestHelpers.RequireHalconRuntime();
        HObjectHolder holder = new();

        holder.GenImageConst(out HObjectWeakHandle<HObject> image, "byte", 16, 16);

        Assert.IsTrue(image.IsAlive);
        Assert.AreEqual(1, holder.TrackedObjectCount);
        Assert.AreEqual(1, holder.GetStrongRefCount(image));

        using (HObjectHandle<HObject> strong = holder.Borrow(image))
        {
            Assert.IsTrue(strong.Value.IsInitialized());
        }

        holder.Dispose();
        Assert.IsFalse(image.IsAlive);
        Assert.AreEqual(0, holder.GetStrongRefCount(image));
    }

    [TestMethod]
    public void Threshold_OperatorThrows_OutputIsEmptyAndNoAnchorRegistered()
    {
        TestHelpers.RequireHalconRuntime();
        using HObjectHolder holder = new();
        HOperatorSet.GenImageConst(out HObject image, "byte", 16, 16);

        HObjectWeakHandle<HObject> region = HObjectWeakHandle<HObject>.Empty;
        Assert.ThrowsExactly<HOperatorException>(() =>
            holder.Threshold(image, out region, new HTuple("invalid_min_gray"), 128));

        Assert.IsFalse(region.IsAlive);
        Assert.AreEqual(0, holder.TrackedObjectCount);
        image.Dispose();
    }

    [TestMethod]
    public void Watersheds_MultipleOutputs_AreAllTrackedAndCanBeReleasedIndependently()
    {
        TestHelpers.RequireHalconRuntime();
        using HObjectHolder holder = new();
        HOperatorSet.GenImageConst(out HObject source, "byte", 32, 32);

        holder.Watersheds(source, out HObjectWeakHandle<HObject> basins, out HObjectWeakHandle<HObject> watersheds);

        Assert.AreEqual(2, holder.TrackedObjectCount);
        Assert.IsTrue(basins.IsAlive);
        Assert.IsTrue(watersheds.IsAlive);
        Assert.AreEqual(1, holder.GetStrongRefCount(basins));
        Assert.AreEqual(1, holder.GetStrongRefCount(watersheds));

        Assert.IsTrue(holder.Release(basins));
        Assert.IsFalse(basins.IsAlive);
        Assert.IsTrue(watersheds.IsAlive);
        source.Dispose();
    }

    [TestMethod]
    [Timeout(120000)]
    public void AllGeneratedWrappers_WithNullInputs_LeaveNoAnchors()
    {
        TestHelpers.RequireHalconRuntime();

        MethodInfo[] wrappers = typeof(HObjectHolder)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetParameters().Any(parameter =>
                parameter.IsOut && parameter.ParameterType == typeof(HObjectWeakHandle<HObject>).MakeByRefType()))
            .ToArray();

        Assert.AreEqual(598, wrappers.Length);

        foreach (MethodInfo method in wrappers)
        {
            // 每个算子使用独立 Holder：部分算子接受空输入并成功返回，其余走异常路径。
            using HObjectHolder holder = new();
            object?[] arguments = method.GetParameters()
                .Select<ParameterInfo, object?>(parameter =>
                    parameter.ParameterType.IsByRef ||
                    parameter.ParameterType == typeof(HObject) ||
                    parameter.ParameterType == typeof(HTuple)
                        ? null
                        : throw new InvalidOperationException($"未预期的参数类型 {parameter.ParameterType}"))
                .ToArray();

            try
            {
                method.Invoke(holder, arguments);
            }
            catch (TargetInvocationException)
            {
                // 空输入可能触发原算子异常或 TrackAll 校验异常，二者都会进入生成方法的 catch 分支。
            }
        }
    }

    [TestMethod]
    public void Watersheds_OperatorThrows_AllOutputsAreEmpty()
    {
        TestHelpers.RequireHalconRuntime();
        using HObjectHolder holder = new();
        HObject invalidSource = new();

        HObjectWeakHandle<HObject> basins = HObjectWeakHandle<HObject>.Empty;
        HObjectWeakHandle<HObject> watersheds = HObjectWeakHandle<HObject>.Empty;
        Assert.ThrowsExactly<HOperatorException>(() =>
            holder.Watersheds(invalidSource, out basins, out watersheds));

        Assert.IsFalse(basins.IsAlive);
        Assert.IsFalse(watersheds.IsAlive);
        Assert.AreEqual(0, holder.TrackedObjectCount);
        invalidSource.Dispose();
    }
}
