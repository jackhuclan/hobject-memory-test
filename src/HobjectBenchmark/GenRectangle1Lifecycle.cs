using HalconDotNet;
using HObjectMemory.Lifetime;

namespace BenchmarkDotNetDemo.Core;

/// <summary>
/// GenRectangle1 的三种生命周期策略，供 GenRectangle1Benchmarks 调用。
/// 算子流程与现有 HObjectLifecycle 保持一致（GenRectangle1 -&gt; AreaCenter/CountObj）。
/// </summary>
public static class GenRectangle1Lifecycle
{
    private static long _sink;

    /// <summary>累计算子结果，避免无用代码被优化掉。</summary>
    public static long Sink => Volatile.Read(ref _sink);

    /// <summary>方式一：创建矩形但不主动释放，依赖 GC/终结器。</summary>
    public static void DirectNoDispose(Rectangle1Case testCase)
    {
        HObject rectangle = null;

        HOperatorSet.GenRectangle1(
            out rectangle,
            new HTuple(testCase.Row1),
            new HTuple(testCase.Column1),
            new HTuple(testCase.Row2),
            new HTuple(testCase.Column2));
        HOperatorSet.AreaCenter(rectangle, out HTuple area, out HTuple row, out HTuple column);
        HOperatorSet.CountObj(rectangle, out HTuple count);

        _sink += area.Length + row.Length + column.Length + count.Length;
    }

    /// <summary>方式二：裸 HObject + try/finally 逆序 Dispose。</summary>
    public static void ManualDispose(Rectangle1Case testCase)
    {
        HObject rectangle = null;

        try
        {
            HOperatorSet.GenRectangle1(
                out rectangle,
                new HTuple(testCase.Row1),
                new HTuple(testCase.Column1),
                new HTuple(testCase.Row2),
                new HTuple(testCase.Column2));
            HOperatorSet.AreaCenter(rectangle, out HTuple area, out HTuple row, out HTuple column);
            HOperatorSet.CountObj(rectangle, out HTuple count);

            _sink += area.Length + row.Length + column.Length + count.Length;
        }
        finally
        {
            rectangle?.Dispose();
        }
    }

    /// <summary>方式三：HObjectHandle&lt;HObject&gt; 引用计数句柄，using 出作用域即确定性释放。</summary>
    public static void HandleDispose(Rectangle1Case testCase)
    {
        HObject rawRectangle = null;

        try
        {
            HOperatorSet.GenRectangle1(
                out rawRectangle,
                new HTuple(testCase.Row1),
                new HTuple(testCase.Column1),
                new HTuple(testCase.Row2),
                new HTuple(testCase.Column2));
            using HObjectHandle<HObject> rectangle = HObjectHandle<HObject>.From(rawRectangle);
            rawRectangle = null; // 所有权已转移

            HOperatorSet.AreaCenter(rectangle.Value, out HTuple area, out HTuple row, out HTuple column);
            HOperatorSet.CountObj(rectangle.Value, out HTuple count);

            _sink += area.Length + row.Length + column.Length + count.Length;
        }
        finally
        {
            // 算子在创建句柄前抛错时的兜底释放。
            rawRectangle?.Dispose();
        }
    }

    /// <summary>触发 Halcon 首次初始化，避免把初始化成本计入基准。</summary>
    public static void Warmup()
    {
        HObject rectangle = null;
        try
        {
            HOperatorSet.GenRectangle1(out rectangle, new HTuple(0), new HTuple(0), new HTuple(7), new HTuple(7));
        }
        finally
        {
            rectangle?.Dispose();
        }
    }
}
