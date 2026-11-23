using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using BenchmarkDotNetDemo.Core;

namespace BenchmarkDotNetDemo.Benchmarks;

/// <summary>
/// 基准配置：InProcess 工具链 + MemoryDiagnoser + 自定义 PrivateBytes 诊断器。
/// </summary>
/// <remarks>
/// InProcess 的意义：Halcon 内存是非托管的，诊断器必须运行在基准所在进程内才能采样 Private Bytes；
/// 同时省去 BenchmarkDotNet 为每个用例生成/编译子项目的时间。
/// 代价是所有用例共享同一进程，因此执行顺序刻意安排为"先干净、后泄漏"，避免污染后续用例的基线。
/// </remarks>
public class HObjectBenchmarkConfig : ManualConfig
{
    public HObjectBenchmarkConfig()
    {
        AddJob(Job.Default
            .WithRuntime(CoreRuntime.Core80)
            .WithToolchain(InProcessEmitToolchain.Instance)
            .WithWarmupCount(3)
            .WithIterationCount(10));

        AddDiagnoser(MemoryDiagnoser.Default, PrivateBytesDiagnoser.Default);

        WithOrderer(new DefaultOrderer(SummaryOrderPolicy.FastestToSlowest));
    }
}

/// <summary>
/// 对比三种 HObject 生命周期策略：执行相同算子流程，观察耗时与内存。
/// </summary>
[Config(typeof(HObjectBenchmarkConfig))]
public class HObjectLifecycleBenchmarks
{
    public const int ImageWidth = 128;
    public const int ImageHeight = 128;

    /// <summary>
    /// 每次基准调用内部循环的 HObject 处理次数，即"迭代次数"。
    /// 通过 Params 覆盖两种规模，可按需修改。
    /// </summary>
    [Params(100, 1000)]
    public int Iterations { get; set; }

    [GlobalSetup]
    public void Setup() => HObjectLifecycle.Warmup();

    /// <summary>HObjectHandle&lt;HObject&gt;：引用计数句柄，using 出作用域时确定性释放。</summary>
    [Benchmark(Baseline = true)]
    public void HandleDispose()
    {
        for (int i = 0; i < Iterations; i++)
        {
            HObjectLifecycle.HandleDispose(ImageWidth, ImageHeight);
        }
    }

    /// <summary>裸 HObject + try/finally 手动 Dispose：同样确定性释放，但需人工登记每个中间对象。</summary>
    [Benchmark]
    public void ManualDispose()
    {
        for (int i = 0; i < Iterations; i++)
        {
            HObjectLifecycle.ManualDispose(ImageWidth, ImageHeight);
        }
    }

    /// <summary>裸 HObject 不主动释放：依赖 GC/终结器，Halcon 非托管内存持续累积。</summary>
    [Benchmark]
    public void DirectNoDispose()
    {
        for (int i = 0; i < Iterations; i++)
        {
            HObjectLifecycle.DirectNoDispose(ImageWidth, ImageHeight);
        }
    }
}
