using HalconDotNet;
using HObjectMemory.Lifetime;

namespace BenchmarkDotNetDemo.Core;

/// <summary>
/// 三种 HObject 生命周期策略，执行完全相同的算子流程：
/// GenImageConst -> Threshold -> Connection -> AreaCenter/CountObj。
/// 唯一差异是资源释放方式。
/// </summary>
public static class HObjectLifecycle
{
    private static long _sink;

    /// <summary>累计算子结果，避免"无用代码"被优化掉；仅用于校验。</summary>
    public static long Sink => Volatile.Read(ref _sink);

    /// <summary>
    /// 方式一（基准问题写法）：直接使用 HObject，不主动 Dispose。
    /// 非托管图像资源只能依赖 GC/终结器，HalconDotNet 24.5 下实测不会被回收。
    /// </summary>
    public static void DirectNoDispose(int width, int height)
    {
        HObject image = null;
        HObject region = null;
        HObject connected = null;

        HOperatorSet.GenImageConst(out image, "byte", width, height);
        HOperatorSet.Threshold(image, out region, new HTuple(100), new HTuple(200));
        HOperatorSet.Connection(region, out connected);
        HOperatorSet.AreaCenter(connected, out HTuple area, out HTuple row, out HTuple column);
        HOperatorSet.CountObj(connected, out HTuple count);

        // 列表场景：创建 List<HObject> 并验证初始化、添加及元素类型/数量。
        List<HObject> objects = new(3) { image, region, connected };
        if (objects is null || objects.Count != 3 ||
            objects[0] is not HObject || objects[1] is not HObject || objects[2] is not HObject)
        {
            throw new InvalidOperationException("List<HObject> 初始化或元素校验失败");
        }

        _sink += area.Length + count.Length + objects.Count;
    }

    /// <summary>
    /// 方式二：裸 HObject + try/finally 手动 Dispose。确定性释放，但每个中间对象都要人工登记。
    /// </summary>
    public static void ManualDispose(int width, int height)
    {
        HObject image = null;
        HObject region = null;
        HObject connected = null;

        try
        {
            HOperatorSet.GenImageConst(out image, "byte", width, height);
            HOperatorSet.Threshold(image, out region, new HTuple(100), new HTuple(200));
            HOperatorSet.Connection(region, out connected);
            HOperatorSet.AreaCenter(connected, out HTuple area, out HTuple row, out HTuple column);
            HOperatorSet.CountObj(connected, out HTuple count);

            // 列表场景：创建 List<HObject> 并验证初始化、添加及元素类型/数量。
            List<HObject> objects = new(3) { image, region, connected };
            if (objects is null || objects.Count != 3 ||
                objects[0] is not HObject || objects[1] is not HObject || objects[2] is not HObject)
            {
                throw new InvalidOperationException("List<HObject> 初始化或元素校验失败");
            }

            _sink += area.Length + count.Length + objects.Count;
        }
        finally
        {
            // 逆序释放；单次 Dispose 即可，无需引用计数。
            connected?.Dispose();
            region?.Dispose();
            image?.Dispose();
        }
    }

    /// <summary>
    /// 方式三：HObjectHandle&lt;HObject&gt; 引用计数句柄。资源所有权进入句柄，
    /// using 离开作用域时引用计数归零、确定性释放，异常路径同样安全。
    /// </summary>
    public static void HandleDispose(int width, int height)
    {
        HObject rawImage = null;
        HObject rawRegion = null;
        HObject rawConnected = null;

        try
        {
            HOperatorSet.GenImageConst(out rawImage, "byte", width, height);
            using HObjectHandle<HObject> image = HObjectHandle<HObject>.From(rawImage);
            rawImage = null; // 所有权已转移给句柄

            HOperatorSet.Threshold(image.Value, out rawRegion, new HTuple(100), new HTuple(200));
            using HObjectHandle<HObject> region = HObjectHandle<HObject>.From(rawRegion);
            rawRegion = null;

            HOperatorSet.Connection(region.Value, out rawConnected);
            using HObjectHandle<HObject> connected = HObjectHandle<HObject>.From(rawConnected);
            rawConnected = null;

            HOperatorSet.AreaCenter(connected.Value, out HTuple area, out HTuple row, out HTuple column);
            HOperatorSet.CountObj(connected.Value, out HTuple count);

            // 列表场景：创建 List<HObjectHandle<HObject>> 并验证初始化、添加及元素类型/数量。
            List<HObjectHandle<HObject>> handles = new(3) { image, region, connected };
            if (handles is null || handles.Count != 3 ||
                handles[0] is not HObjectHandle<HObject> ||
                handles[1] is not HObjectHandle<HObject> ||
                handles[2] is not HObjectHandle<HObject> ||
                handles[0].Value is not HObject ||
                handles[1].Value is not HObject ||
                handles[2].Value is not HObject)
            {
                throw new InvalidOperationException("List<HObjectHandle<HObject>> 初始化或元素校验失败");
            }

            _sink += area.Length + count.Length + handles.Count;
        }
        finally
        {
            // 异常路径兜底：算子在创建句柄前抛错时释放裸对象；正常路径三者已置 null。
            rawImage?.Dispose();
            rawRegion?.Dispose();
            rawConnected?.Dispose();
        }
    }

    /// <summary>触发 Halcon 首次初始化（加载原生库、许可证），避免把初始化成本算进基准。</summary>
    public static void Warmup()
    {
        HObject image = null;
        try
        {
            HOperatorSet.GenImageConst(out image, "byte", 8, 8);
        }
        finally
        {
            image?.Dispose();
        }
    }
}
