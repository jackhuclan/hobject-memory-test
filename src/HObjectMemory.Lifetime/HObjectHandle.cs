namespace HObjectMemory.Lifetime;

/// <summary>
/// 共享所有权的资源句柄。最后一个强句柄释放时，底层资源被释放。
/// </summary>
/// <remarks>
/// IsEmpty 描述的是句柄状态，不代表底层 HALCON 图像对象为空；由 GenEmptyObj 产生的空图像仍会被正常释放。
/// Value 返回的底层对象不得逃逸到比句柄更长的生命周期中。
/// </remarks>
/// <typeparam name="T">可释放的资源类型，例如 HalconDotNet.HObject。</typeparam>
public sealed class HObjectHandle<T> : IDisposable, IEquatable<HObjectHandle<T>> where T : class, IDisposable
{
    // null 表示空句柄（尚未持有资源或已释放），这是该类型的合法状态。
    private RefCountedOwner<T>? _owner;
    private int _disposed;

    internal HObjectHandle(RefCountedOwner<T>? owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// 取得资源的所有权；传入 null 时创建空句柄。
    /// </summary>
    /// <param name="value">资源实例。</param>
    public HObjectHandle(T? value)
        : this(value, isAlive: null)
    {
    }

    internal HObjectHandle(T? value, Func<T, bool>? isAlive)
    {
        if (value is null)
        {
            return;
        }

        _owner = new RefCountedOwner<T>(value, isAlive);
    }

    /// <summary>
    /// 诊断用：读取底层控制块，不延长生命周期。
    /// </summary>
    internal RefCountedOwner<T>? Owner => Volatile.Read(ref _owner);

    /// <summary>
    /// 空句柄。所有操作均安全，且不持有任何资源。
    /// </summary>
    public static HObjectHandle<T> Empty { get; } = new HObjectHandle<T>((RefCountedOwner<T>?)null);

    /// <summary>
    /// 句柄是否不持有有效资源。
    /// </summary>
    public bool IsEmpty
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return true;
            }

            RefCountedOwner<T>? owner = Volatile.Read(ref _owner);
            return owner is null || owner.IsDisposed;
        }
    }

    /// <summary>
    /// 底层资源。资源已释放时抛出 <see cref="ObjectDisposedException"/>。
    /// </summary>
    public T Value
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(HObjectHandle<T>));
            }

            RefCountedOwner<T>? owner = Volatile.Read(ref _owner);
            if (owner is null)
            {
                throw new ObjectDisposedException(nameof(HObjectHandle<T>));
            }

            return owner.Value;
        }
    }

    /// <summary>
    /// 取得资源的所有权。
    /// </summary>
    /// <param name="value">资源实例。</param>
    /// <returns>持有该资源的强句柄。</returns>
    public static HObjectHandle<T> From(T value)
    {
        return new HObjectHandle<T>(value);
    }

    /// <summary>
    /// 拷贝句柄，增加强引用计数。空句柄返回空句柄，已释放句柄抛出异常。
    /// </summary>
    /// <returns>共享同一资源的新强句柄。</returns>
    public HObjectHandle<T> Share()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(HObjectHandle<T>));
        }

        RefCountedOwner<T>? owner = Volatile.Read(ref _owner);
        if (owner is null)
        {
            return Empty;
        }

        if (!owner.TryAddStrongRef())
        {
            throw new ObjectDisposedException(nameof(HObjectHandle<T>));
        }

        return new HObjectHandle<T>(owner);
    }

    /// <summary>
    /// 把所有权转移给新句柄，当前句柄立即变为空。
    /// </summary>
    /// <returns>接管资源的新强句柄。</returns>
    public HObjectHandle<T> Move()
    {
        RefCountedOwner<T>? owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null)
        {
            return Empty;
        }

        Volatile.Write(ref _disposed, 1);
        if (owner.IsDisposed)
        {
            return Empty;
        }

        return new HObjectHandle<T>(owner);
    }

    /// <summary>
    /// 尝试读取底层资源，资源已释放时返回 false 且 value 为 null。
    /// </summary>
    /// <param name="value">读取到的资源，失败时为 null。</param>
    /// <returns>是否成功读取。</returns>
    public bool TryGetValue(out T? value)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            value = null;
            return false;
        }

        RefCountedOwner<T>? owner = Volatile.Read(ref _owner);
        if (owner is null)
        {
            value = null;
            return false;
        }

        return owner.TryGetValue(out value);
    }

    /// <summary>
    /// 创建不延长资源生命周期的弱观察者。
    /// </summary>
    /// <returns>弱句柄。</returns>
    public HObjectWeakHandle<T> AsWeak()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return HObjectWeakHandle<T>.Empty;
        }

        RefCountedOwner<T>? owner = Volatile.Read(ref _owner);
        if (owner is null || owner.IsDisposed)
        {
            return HObjectWeakHandle<T>.Empty;
        }

        owner.AddWeakRef();
        return new HObjectWeakHandle<T>(owner);
    }

    /// <summary>
    /// 释放当前句柄持有的强引用。可重复调用。
    /// </summary>
    public void Dispose()
    {
        RefCountedOwner<T>? owner = Interlocked.Exchange(ref _owner, null);
        if (owner != null)
        {
            Volatile.Write(ref _disposed, 1);
            owner.ReleaseStrong();
        }
    }

    /// <inheritdoc />
    public bool Equals(HObjectHandle<T>? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        RefCountedOwner<T>? owner = Volatile.Read(ref _owner);
        return owner != null && ReferenceEquals(owner, Volatile.Read(ref other._owner));
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return Equals(obj as HObjectHandle<T>);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        RefCountedOwner<T>? owner = Volatile.Read(ref _owner);
        return owner is null ? 0 : owner.GetHashCode();
    }
}
