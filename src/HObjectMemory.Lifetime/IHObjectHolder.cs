using HalconDotNet;

namespace HObjectMemory.Lifetime;

/// <summary>
/// <see cref="HObjectHolder"/> 的公共契约：包装 <c>HOperatorSet</c> 中以 <c>out HObject</c> 输出结果的方法，
/// 并通过弱句柄暴露结果、由实现持有确定性释放所需的强引用锚点。
/// </summary>
/// <remarks>
/// 契约包含本文件中定义的核心生命周期成员、由 <see cref="IDisposable"/> 继承的 <c>Dispose()</c>，
/// 以及 <c>IHObjectHolder.Generated.cs</c> 中逐一生成的算子包装方法。
/// 实现必须保证：包装方法的返回类型、参数名、参数顺序和调用语义与原算子一致，仅把 <c>out HObject</c> 替换为
/// <c>out HObjectWeakHandle&lt;HObject&gt;</c>；容器释放后调用包装方法抛出 <see cref="ObjectDisposedException"/>；
/// 强引用计数归零时底层 HObject 只被释放一次。
/// </remarks>
public partial interface IHObjectHolder : IDisposable
{
    /// <summary>
    /// 当前由实现持有强引用锚点的 HObject 数量。
    /// </summary>
    int TrackedObjectCount { get; }

    /// <summary>
    /// 获取容器是否已释放；释放后调用包装方法会抛出 <see cref="ObjectDisposedException"/>。
    /// </summary>
    bool IsDisposed { get; }

    /// <summary>
    /// 读取弱句柄对应控制块的当前强引用计数，包含容器锚点和已提升的强句柄。
    /// 返回 0 表示控制块已不存在或弱句柄已释放。
    /// </summary>
    int GetStrongRefCount(HObjectWeakHandle<HObject> weak);

    /// <summary>
    /// 尝试把弱句柄提升为强句柄；底层 HObject 已被外部释放时返回 false。
    /// 成功时调用方负责释放返回的强句柄。
    /// </summary>
    bool TryBorrow(HObjectWeakHandle<HObject> weak, out HObjectHandle<HObject> handle);

    /// <summary>
    /// 把弱句柄提升为强句柄；失败时抛出 <see cref="ObjectDisposedException"/>。
    /// 调用方负责释放返回的强句柄。
    /// </summary>
    HObjectHandle<HObject> Borrow(HObjectWeakHandle<HObject> weak);

    /// <summary>
    /// 提前释放单个 HObject 的容器锚点；若仍有其他强句柄，底层资源延后到最后一个强句柄释放时再释放。
    /// 返回是否找到并释放了对应锚点；重复释放返回 false。
    /// </summary>
    bool Release(HObjectWeakHandle<HObject> weak);
}
