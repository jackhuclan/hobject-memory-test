using HalconDotNet;

namespace HObjectMemory.Lifetime;

/// <summary>
/// HOperatorSet 中 out HObject 算子的生命周期包装器：输出改为 <see cref="HObjectWeakHandle{T}"/>，
/// 由本类为每个输出 HObject 保留一个强引用锚点，并在锚点释放后由 <see cref="RefCountedOwner{T}"/>
/// 在最后一个强引用归零时确定性 Dispose 底层 HObject。
/// </summary>
/// <remarks>
/// 典型用法：<c>using var holder = new HObjectHolder(); holder.Threshold(image, out var region, 0, 128);</c>
/// 需要把 weak 传给普通 Halcon 算子时，用 <see cref="Borrow"/> / <see cref="TryBorrow"/> 取得强句柄，
/// 并 Dispose 该强句柄。不要从 <see cref="HObjectHandle{T}.Value"/> 逃逸出原始 HObject 的长期引用。
/// 生成的包装方法位于 HObjectHolder.Generated.cs，由 tools/HObjectHolderGenerator 产出。
/// </remarks>
public sealed partial class HObjectHolder : IDisposable
{
    private readonly object _gate = new();
    private readonly List<HObjectHandle<HObject>> _anchors = new();
    private bool _disposed;

    public HObjectHolder()
    {
    }

    ~HObjectHolder()
    {
        // 安全网：忘记 using 时仍释放锚点；不要在终结器中向调用方传播异常。
        Dispose(disposing: false);
    }

    /// <summary>本容器是否已释放。已释放后调用任何包装方法都会抛出 <see cref="ObjectDisposedException"/>。</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <summary>当前由本容器持有强引用的 HObject 数量。</summary>
    public int TrackedObjectCount
    {
        get
        {
            lock (_gate)
            {
                return _anchors.Count;
            }
        }
    }

    /// <summary>
    /// 读取某个弱句柄对应控制块的强引用计数（含本容器锚点与已提升的强句柄）。
    /// 返回 0 表示控制块已不存在或弱句柄已释放。
    /// </summary>
    public int GetStrongRefCount(HObjectWeakHandle<HObject> weak)
    {
        ArgumentNullException.ThrowIfNull(weak);
        return weak.Owner?.StrongCount ?? 0;
    }

    /// <summary>
    /// 尝试把弱句柄提升为强句柄。底层 HObject 已被外部 Dispose 时返回 false。
    /// </summary>
    public bool TryBorrow(HObjectWeakHandle<HObject> weak, out HObjectHandle<HObject> handle)
    {
        ArgumentNullException.ThrowIfNull(weak);
        return weak.TryGetTarget(out handle);
    }

    /// <summary>
    /// 把弱句柄提升为强句柄；失败时抛出 <see cref="ObjectDisposedException"/>。
    /// 调用方负责 Dispose 返回的强句柄。
    /// </summary>
    public HObjectHandle<HObject> Borrow(HObjectWeakHandle<HObject> weak)
    {
        if (!TryBorrow(weak, out HObjectHandle<HObject> handle))
        {
            throw new ObjectDisposedException(nameof(HObjectWeakHandle<HObject>), "底层 HObject 已释放或弱句柄已失效。");
        }

        return handle;
    }

    /// <summary>
    /// 提前释放单个 HObject 的容器锚点；若仍有其他强句柄，资源会在最后一个强句柄释放时才被 Dispose。
    /// </summary>
    /// <returns>是否找到并释放了对应锚点。</returns>
    public bool Release(HObjectWeakHandle<HObject> weak)
    {
        ArgumentNullException.ThrowIfNull(weak);
        RefCountedOwner<HObject>? owner = weak.Owner;
        if (owner is null)
        {
            return false;
        }

        HObjectHandle<HObject>? anchor = null;
        lock (_gate)
        {
            for (int i = 0; i < _anchors.Count; i++)
            {
                if (ReferenceEquals(_anchors[i].Owner, owner))
                {
                    anchor = _anchors[i];
                    _anchors.RemoveAt(i);
                    break;
                }
            }
        }

        anchor?.Dispose();
        return anchor is not null;
    }

    /// <summary>可重复调用；释放全部锚点，使没有外部强句柄的 HObject 立即被 Dispose。</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        HObjectHandle<HObject>[] anchors;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            anchors = _anchors.ToArray();
            _anchors.Clear();
        }

        List<Exception>? failures = null;
        foreach (HObjectHandle<HObject> anchor in anchors)
        {
            try
            {
                anchor.Dispose();
            }
            catch (Exception ex)
            {
                // 继续释放其余对象；显式 Dispose 时汇总异常，终结路径中吞掉。
                if (disposing)
                {
                    (failures ??= []).Add(ex);
                }
            }
        }

        if (failures is { Count: > 0 })
        {
            throw new AggregateException("释放 HObject 锚点时发生异常。", failures);
        }
    }

    /// <summary>
    /// 包装方法的统一提交点：先创建全部强句柄与弱观察者，再一次性登记锚点。
    /// 任一步失败都不会留下部分登记或泄漏的原始 HObject。
    /// </summary>
    internal HObjectWeakHandle<HObject>[] TrackAll(HObject[] values, string operatorName)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(operatorName);

        if (values.Length == 0)
        {
            return [];
        }

        List<HObjectHandle<HObject>> handles = new(values.Length);
        List<HObjectWeakHandle<HObject>> weaks = new(values.Length);
        bool committed = false;

        try
        {
            foreach (HObject value in values)
            {
                if (!IsHObjectAlive(value))
                {
                    throw new InvalidOperationException($"{operatorName} 输出了未初始化或已释放的 HObject。");
                }

                HObjectHandle<HObject> handle = new(value, static candidate => IsHObjectAlive(candidate));
                handles.Add(handle);
                weaks.Add(handle.AsWeak());
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(HObjectHolder));
                }

                foreach (HObjectHandle<HObject> handle in handles)
                {
                    _anchors.Add(handle);
                }
            }

            committed = true;
            return weaks.ToArray();
        }
        catch
        {
            if (!committed)
            {
                lock (_gate)
                {
                    foreach (HObjectHandle<HObject> handle in handles)
                    {
                        _anchors.Remove(handle);
                    }
                }
            }

            foreach (HObjectWeakHandle<HObject> weak in weaks)
            {
                SafeDisposeWeak(weak);
            }

            foreach (HObjectHandle<HObject> handle in handles)
            {
                SafeDisposeHandle(handle);
            }

            throw;
        }
    }

    /// <summary>
    /// 算子抛异常时释放已经写入的 out HObject；吞掉二次释放异常，避免掩盖原始算子异常。
    /// </summary>
    internal static void DisposeRawOutputs(HObject[] values)
    {
        foreach (HObject value in values)
        {
            try
            {
                if (value is not null && value.IsInitialized())
                {
                    value.Dispose();
                }
            }
            catch
            {
                // 异常路径以原始算子异常为准。
            }
        }
    }

    private static bool IsHObjectAlive(HObject value)
    {
        try
        {
            return value is not null && value.IsInitialized();
        }
        catch
        {
            // 探测失败按已失效处理，绝不把可疑对象交给调用方。
            return false;
        }
    }

    private static void SafeDisposeHandle(HObjectHandle<HObject>? handle)
    {
        try
        {
            handle?.Dispose();
        }
        catch
        {
            // 回滚路径不覆盖原始异常。
        }
    }

    private static void SafeDisposeWeak(HObjectWeakHandle<HObject>? weak)
    {
        try
        {
            weak?.Dispose();
        }
        catch
        {
            // 回滚路径不覆盖原始异常。
        }
    }
}
