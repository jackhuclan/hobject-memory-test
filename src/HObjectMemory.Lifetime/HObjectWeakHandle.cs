namespace HObjectMemory.Lifetime;

/// <summary>
/// 弱观察者。不延长资源生命周期，可在资源仍存活时安全提升为强句柄。
/// </summary>
/// <typeparam name="T">可释放的资源类型，例如 HalconDotNet.HObject。</typeparam>
public sealed class HObjectWeakHandle<T> : IDisposable where T : class, IDisposable
{
    // null 表示空观察者（Dispose 后或初始空实例），这是该类型的合法状态。
    private RefCountedOwner<T>? _owner;

    internal HObjectWeakHandle(RefCountedOwner<T>? owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// 诊断用：读取底层控制块，不延长生命周期。
    /// </summary>
    internal RefCountedOwner<T>? Owner => Volatile.Read(ref _owner);

    /// <summary>
    /// 空弱句柄。所有操作均安全。
    /// </summary>
    public static HObjectWeakHandle<T> Empty { get; } = new HObjectWeakHandle<T>(null);

    /// <summary>
    /// 被观察的资源是否仍然存活。
    /// </summary>
    public bool IsAlive
    {
        get
        {
            RefCountedOwner<T>? owner = Volatile.Read(ref _owner);
            return owner != null && !owner.IsDisposed;
        }
    }

    /// <summary>
    /// 尝试把弱引用提升为强句柄。资源已释放时返回 false，不会暴露已释放对象。
    /// </summary>
    /// <param name="target">提升得到的强句柄，失败时为空句柄。</param>
    /// <returns>是否提升成功。</returns>
    public bool TryGetTarget(out HObjectHandle<T> target)
    {
        RefCountedOwner<T>? owner = Volatile.Read(ref _owner);
        if (owner is null || !owner.TryAddStrongRef())
        {
            target = HObjectHandle<T>.Empty;
            return false;
        }

        target = new HObjectHandle<T>(owner);
        return true;
    }

    /// <summary>
    /// 释放当前弱观察者持有的弱计数。可重复调用。
    /// </summary>
    public void Dispose()
    {
        RefCountedOwner<T>? owner = Interlocked.Exchange(ref _owner, null);
        if (owner != null)
        {
            owner.ReleaseWeak();
        }
    }
}
