namespace HObjectMemory.Lifetime;

/// <summary>
/// 引用计数控制块。强引用归零时释放负载，弱引用归零前保持控制块存活。
/// </summary>
/// <typeparam name="T">需要确定性释放的资源类型。</typeparam>
internal sealed class RefCountedOwner<T> where T : class, IDisposable
{
    // 强引用归零后置 null，表示负载已释放；IsDisposed / TryGetValue 依赖这一状态。
    private T? _value;
    private readonly Func<T, bool>? _isAlive;
    private int _strongCount;
    private int _weakCount;

    /// <summary>
    /// 创建一个强引用计数为 1 的控制块。
    /// </summary>
    /// <param name="value">被管理的资源。</param>
    /// <param name="isAlive">可选的外部释放探测；返回 false 时视为负载已释放。</param>
    public RefCountedOwner(T value, Func<T, bool>? isAlive = null)
    {
        _value = value;
        _isAlive = isAlive;
        _strongCount = 1;
        _weakCount = 1;
    }

    public bool IsDisposed => !IsAlive(Volatile.Read(ref _value));

    /// <summary>
    /// 当前强引用计数。用于诊断；归零即触发负载释放。
    /// </summary>
    public int StrongCount => Volatile.Read(ref _strongCount);

    public T Value
    {
        get
        {
            T? value = Volatile.Read(ref _value);
            if (!IsAlive(value))
            {
                throw new ObjectDisposedException(nameof(RefCountedOwner<T>));
            }

            return value!;
        }
    }

    public bool TryGetValue(out T? value)
    {
        value = Volatile.Read(ref _value);
        if (!IsAlive(value))
        {
            value = null;
            return false;
        }

        return true;
    }

    public bool TryAddStrongRef()
    {
        while (true)
        {
            if (!IsAlive(Volatile.Read(ref _value)))
            {
                return false;
            }

            int count = Volatile.Read(ref _strongCount);
            if (count == 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _strongCount, count + 1, count) != count)
            {
                continue;
            }

            // 外部可能在 CAS 之前刚释放负载；增加计数后再次确认，避免提升出已失效对象。
            if (IsAlive(Volatile.Read(ref _value)))
            {
                return true;
            }

            ReleaseStrong();
            return false;
        }
    }

    private bool IsAlive(T? value)
    {
        if (value is null)
        {
            return false;
        }

        if (_isAlive is null)
        {
            return true;
        }

        try
        {
            return _isAlive(value);
        }
        catch
        {
            // 探测失败一律按失效处理，绝不把可能已释放的资源交给调用方。
            return false;
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

        T? value = Interlocked.Exchange(ref _value, null);
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
