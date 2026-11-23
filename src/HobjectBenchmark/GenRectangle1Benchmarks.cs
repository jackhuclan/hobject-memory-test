using BenchmarkDotNet.Attributes;
using BenchmarkDotNetDemo.Core;
using HObjectMemory.Lifetime;

namespace BenchmarkDotNetDemo.Benchmarks;

/// <summary>
/// GenRectangle1 算子独立基准：复用 HObjectBenchmarkConfig，
/// 覆盖正常矩形、不同尺寸与典型边界参数，三种生命周期策略并列对比。
/// </summary>
/// <remarks>
/// 使用 --filter "*GenRectangle1Benchmarks*" 可只运行本组，不影响现有 HObjectLifecycleBenchmarks。
/// </remarks>
[Config(typeof(HObjectBenchmarkConfig))]
public class GenRectangle1Benchmarks
{
    /// <summary>每次基准调用内部的 GenRectangle1 处理次数；可修改后重新构建。</summary>
    [Params(200)]
    public int Iterations { get; set; }

    /// <summary>以用例名作为参数，汇总表中直接显示可读名称。</summary>
    [ParamsSource(nameof(CaseNames))]
    public string CaseName { get; set; } = string.Empty;

    public static IEnumerable<string> CaseNames => Rectangle1TestData.Cases.Select(c => c.Name);

    private Rectangle1Case Case => Rectangle1TestData.Cases.First(c => c.Name == CaseName);

    [GlobalSetup]
    public void Setup() => GenRectangle1Lifecycle.Warmup();

    /// <summary>HObjectHandle&lt;HObject&gt;：引用计数句柄，确定性释放（Baseline）。</summary>
    [Benchmark(Baseline = true)]
    public void HandleDispose()
    {
        for (int i = 0; i < Iterations; i++)
        {
            GenRectangle1Lifecycle.HandleDispose(Case);
        }
    }

    /// <summary>裸 HObject + try/finally 手动 Dispose。</summary>
    [Benchmark]
    public void ManualDispose()
    {
        for (int i = 0; i < Iterations; i++)
        {
            GenRectangle1Lifecycle.ManualDispose(Case);
        }
    }

    /// <summary>不主动释放：依赖 GC/终结器。</summary>
    [Benchmark]
    public void DirectNoDispose()
    {
        for (int i = 0; i < Iterations; i++)
        {
            GenRectangle1Lifecycle.DirectNoDispose(Case);
        }
    }
}
