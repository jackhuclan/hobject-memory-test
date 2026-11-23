using System.Collections.Concurrent;
using System.Diagnostics;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;

namespace BenchmarkDotNetDemo.Benchmarks;

/// <summary>
/// 输出进程私有字节（Private Bytes）的增量和峰值，单位 MB。
/// </summary>
/// <remarks>
/// MemoryDiagnoser 只统计托管堆分配；Halcon 的图像数据在非托管内存中，
/// 托管侧的 Allocated 列完全看不到这部分内存。此诊断器在 ActualRun 阶段
/// 用后台线程按 5ms 间隔采样 PrivateMemorySize64，在 ProcessResults 中
/// 以 Metric 的形式输出"结束增量"与"峰值增量"两列。
/// 设计为与 InProcess 工具链配合使用（采样线程必须运行在基准所在进程内）。
/// </remarks>
public sealed class PrivateBytesDiagnoser : IDiagnoser
{
    public static readonly PrivateBytesDiagnoser Default = new PrivateBytesDiagnoser();

    private const long MiB = 1024L * 1024L;

    private readonly ConcurrentDictionary<string, RunStats> _stats = new ConcurrentDictionary<string, RunStats>();

    private readonly object _gate = new object();
    private volatile bool _stop;
    private Thread? _sampler;
    private string _key = string.Empty;
    private long _baselineBytes;
    private long _peakBytes;

    private PrivateBytesDiagnoser()
    {
    }

    /// <summary>ExtraRun 模式：不参与计时，只在结果中输出指标。</summary>
    public RunMode GetRunMode(BenchmarkCase benchmarkCase) => RunMode.ExtraRun;

    public IEnumerable<string> Ids => new[] { "PrivateBytes" };

    public IEnumerable<IExporter> Exporters => Array.Empty<IExporter>();

    public IEnumerable<IAnalyser> Analysers => Array.Empty<IAnalyser>();

    public void DisplayResults(ILogger logger)
    {
    }

    public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters)
    {
        yield break;
    }

    public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
    {
        switch (signal)
        {
            case HostSignal.BeforeActualRun:
                StartSampling(parameters.BenchmarkCase?.ToString() ?? "unknown");
                break;

            case HostSignal.AfterActualRun:
                StopSampling();
                break;
        }
    }

    /// <summary>
    /// ActualRun 结束后（AfterActualRun 信号之后）由引擎回调，
    /// 把采样得到的私有字节增量作为 Metric 输出到汇总表。
    /// </summary>
    public IEnumerable<Metric> ProcessResults(DiagnoserResults diagnoserResults)
    {
        string key = diagnoserResults.BenchmarkCase?.ToString() ?? "unknown";
        if (_stats.TryGetValue(key, out RunStats? stats))
        {
            yield return new Metric(PrivateBytesMetricDescriptor.Delta, stats.DeltaBytes / (double)MiB);
            yield return new Metric(PrivateBytesMetricDescriptor.PeakDelta, stats.PeakDeltaBytes / (double)MiB);
        }
    }

    private void StartSampling(string key)
    {
        lock (_gate)
        {
            _key = key;
            _stop = false;
            _baselineBytes = ReadPrivateBytes();
            _peakBytes = _baselineBytes;

            _sampler = new Thread(SampleLoop)
            {
                IsBackground = true,
                Name = nameof(PrivateBytesDiagnoser) + ".Sampler"
            };
            _sampler.Start();
        }
    }

    private void StopSampling()
    {
        lock (_gate)
        {
            _stop = true;
            _sampler?.Join(2000);
            _sampler = null;

            long endBytes = ReadPrivateBytes();
            _stats[_key] = new RunStats
            {
                BaselineBytes = _baselineBytes,
                EndBytes = endBytes,
                PeakBytes = Math.Max(_peakBytes, endBytes)
            };
        }
    }

    private static long ReadPrivateBytes()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    private void SampleLoop()
    {
        using Process process = Process.GetCurrentProcess();
        while (!_stop)
        {
            process.Refresh();
            long current = process.PrivateMemorySize64;
            if (current > _peakBytes)
            {
                _peakBytes = current;
            }

            Thread.Sleep(5);
        }
    }

    private sealed class RunStats
    {
        public long BaselineBytes;
        public long EndBytes;
        public long PeakBytes;

        public long DeltaBytes => EndBytes - BaselineBytes;
        public long PeakDeltaBytes => PeakBytes - BaselineBytes;
    }

    /// <summary>把私有字节增量暴露为汇总表列的描述符。</summary>
    private sealed class PrivateBytesMetricDescriptor : IMetricDescriptor
    {
        internal static readonly PrivateBytesMetricDescriptor Delta =
            new PrivateBytesMetricDescriptor(
                "PrivateBytes.Delta",
                "PrivateBytes增量(MB)",
                "ActualRun 阶段结束时进程私有字节的净增量（含 Halcon 非托管内存）");

        internal static readonly PrivateBytesMetricDescriptor PeakDelta =
            new PrivateBytesMetricDescriptor(
                "PrivateBytes.PeakDelta",
                "PrivateBytes峰值增量(MB)",
                "ActualRun 阶段按 5ms 间隔采样到的私有字节峰值增量");

        private PrivateBytesMetricDescriptor(string id, string displayName, string legend)
        {
            Id = id;
            DisplayName = displayName;
            Legend = legend;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string Legend { get; }

        public string NumberFormat => "0.##";

        public UnitType UnitType => UnitType.Dimensionless;

        public string Unit => "MB";

        public bool TheGreaterTheBetter => false;

        public int PriorityInCategory => 0;

        public bool GetIsAvailable(Metric metric) => true;
    }
}
