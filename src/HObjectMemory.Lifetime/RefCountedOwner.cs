namespace HObjectMemory.Lifetime;

/// <summary>
/// 引用计数控制块。强引用归零时释放负载，弱引用归零前保持控制块存活。
/// </summary>
/// <typeparam name="T">需要确定性释放的资源类型。</typeparam>
internal sealed class RefCountedOwner<T> where T : class, IDisposable
{
    private T _value;
    private int _strongCount;
    private int _weakCount;

    /// <summary>
    /// 创建一个强引用计数为 1 的控制块。
    /// </summary>
    /// <param name="value">被管理的资源。</param>
    public RefCountedOwner(T value)
    {
        _value = value;
        _strongCount = 1;
        _weakCount = 1;
    }

    public bool IsDisposed => Volatile.Read(ref _value) is null;

    public T Value
    {
        get
        {
            T value = Volatile.Read(ref _value);
            if (value is null)
            {
                throw new ObjectDisposedException(nameof(RefCountedOwner<T>));
            }

            return value;
        }
    }

    public bool TryGetValue(out T value)
    {
        value = Volatile.Read(ref _value);
        return value is not null;
    }

    public bool TryAddStrongRef()
    {
        while (true)
        {
            int count = Volatile.Read(ref _strongCount);
            if (count == 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _strongCount, count + 1, count) == count)
            {
                return true;
            }
        }
    }

    public void AddWeakRef()
    {
        Interlocked.Increment(ref _weakCount);
    }

    public void ReleaseStrong()
    {
        if (Interlocked.Decrement(ref _strongCount) != 0)
        {
            return;
        }

        T value = Interlocked.Exchange(ref _value, null);
        try
        {
            value?.Dispose();
        }
        finally
        {
            ReleaseWeak();
        }
    }

    public void ReleaseWeak()
    {
        Interlocked.Decrement(ref _weakCount);
    }
}
