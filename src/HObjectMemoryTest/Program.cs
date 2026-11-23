using HalconDotNet;
using HObjectMemory.Lifetime;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace HObjectMemoryTest;

/// <summary>
/// 对比两种 HObject 生命周期策略：
///   1) Direct  —— 直接使用 HObject，不主动 Dispose；依赖 GC/终结器。
///   2) Handle  —— 用用户自定义的 HObjectHandle&lt;HObject&gt; 包装，离开作用域时确定性释放。
///
/// 两组执行完全相同的 Halcon 算子流程（GenImageConst -&gt; Threshold -&gt; Connection -&gt; AreaCenter/CountObj），
/// 唯一差异是资源生命周期的管理方式。
/// </summary>
internal static class Program
{
    private const long MiB = 1024L * 1024L;

    private static readonly Process CurrentProcess = Process.GetCurrentProcess();

    // 累计算子结果，避免任何形式的"无用代码"优化；仅用于输出校验。
    private static long _sink;

    private sealed class Options
    {
        public int Iterations = 2000;
        public int Warmup = 200;
        public int Width = 512;
        public int Height = 512;
        public int SampleEvery; // 0 = 自动
        public string Group = "both";
    }

    private sealed class BenchmarkResult
    {
        public string Name;
        public int Iterations;
        public double TotalMilliseconds;
        public long BaselineBytes;
        public long EndBytes;
        public long PeakBytes;
        public long AfterGcBytes;
        public int Gc0;
        public int Gc1;
        public int Gc2;

        public long DeltaBytes => EndBytes - BaselineBytes;
        public long PeakDeltaBytes => PeakBytes - BaselineBytes;
        public long AfterGcDeltaBytes => AfterGcBytes - BaselineBytes;
        public double AverageMicroseconds => TotalMilliseconds * 1000.0 / Math.Max(1, Iterations);
    }

    private static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "-h" || args[0] == "--help"))
        {
            PrintUsage();
            return 0;
        }

        Options options;
        try
        {
            options = ParseOptions(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("参数错误: " + ex.Message);
            PrintUsage();
            return 1;
        }

        if (!VerifyHandleSemantics())
        {
            Console.Error.WriteLine("HObjectHandle 语义自校验失败，已中止基准测试。");
            return 1;
        }
        Console.WriteLine("[自检] HObjectHandle 共享/转移/幂等/弱引用语义全部通过。");

        Console.WriteLine("=== Halcon HObject 生命周期基准测试 ===");
        Console.WriteLine($"配置: iterations={options.Iterations}, warmup={options.Warmup}, " +
                          $"image={options.Width}x{options.Height}, group={options.Group}");
        Console.WriteLine($"环境: {RuntimeInformation.FrameworkDescription}, x64={Environment.Is64BitProcess}, " +
                          $"HalconDotNet={typeof(HOperatorSet).Assembly.GetName().Version}");
        Console.WriteLine();

        // 默认先跑 Handle 组：Direct 组不主动释放，若放在前面会抬高后一组的基线。
        string[] order = options.Group switch
        {
            "both" => new[] { "Handle", "Direct" },
            "direct" => new[] { "Direct" },
            "handle" => new[] { "Handle" },
            _ => throw new ArgumentException("--group 只能是 both / direct / handle")
        };

        var results = new Dictionary<string, BenchmarkResult>(StringComparer.OrdinalIgnoreCase);
        Action<int, int> handleBody = RunOnceWithHandle;
        Action<int, int> directBody = RunOnceWithDirectHObject;

        foreach (string name in order)
        {
            Action<int, int> body = name == "Direct" ? directBody : handleBody;

            // 预热：抵消 JIT、Halcon 首次初始化和内部缓存的影响。
            for (int i = 0; i < options.Warmup; i++)
            {
                body(options.Width, options.Height);
            }

            ForceFullGc();
            BenchmarkResult result = Measure(name, options, body);
            results[name] = result;
            Console.WriteLine($"[{name}] 完成: {result.TotalMilliseconds:F1} ms, " +
                              $"内存增量 {FormatMiB(result.DeltaBytes)}, 峰值增量 {FormatMiB(result.PeakDeltaBytes)}");
        }

        Console.WriteLine();
        PrintReport(results, order, options);
        Console.WriteLine($"校验值 sink={_sink}（非 0 表示算子结果确实被计算）");

        return 0;
    }

    /// <summary>
    /// A 组：直接使用 HObject，故意不调用 Dispose。
    /// </summary>
    private static void RunOnceWithDirectHObject(int width, int height)
    {
        HObject image = null;
        HObject region = null;
        HObject connected = null;

        // 没有 finally / Dispose：托管包装对象离开作用域后只能等待 GC 终结器。
        HOperatorSet.GenImageConst(out image, "byte", width, height);
        HOperatorSet.Threshold(image, out region, new HTuple(100), new HTuple(200));
        HOperatorSet.Connection(region, out connected);
        HOperatorSet.AreaCenter(connected, out HTuple area, out HTuple row, out HTuple column);
        HOperatorSet.CountObj(connected, out HTuple count);

        _sink += area.Length + row.Length + column.Length + count.Length;
    }

    /// <summary>
    /// B 组：同样的算子流程，但每个 HObject 立即交由 HObjectHandle&lt;HObject&gt; 管理。
    /// 句柄离开作用域时引用计数归零，底层 HObject 被确定性 Dispose。
    /// </summary>
    private static void RunOnceWithHandle(int width, int height)
    {
        HObject rawImage = null;
        HObject rawRegion = null;
        HObject rawConnected = null;

        try
        {
            HOperatorSet.GenImageConst(out rawImage, "byte", width, height);
            using HObjectHandle<HObject> image = HObjectHandle<HObject>.From(rawImage);
            rawImage = null; // 所有权已转移，原变量不再使用

            HOperatorSet.Threshold(image.Value, out rawRegion, new HTuple(100), new HTuple(200));
            using HObjectHandle<HObject> region = HObjectHandle<HObject>.From(rawRegion);
            rawRegion = null;

            HOperatorSet.Connection(region.Value, out rawConnected);
            using HObjectHandle<HObject> connected = HObjectHandle<HObject>.From(rawConnected);
            rawConnected = null;

            HOperatorSet.AreaCenter(connected.Value, out HTuple area, out HTuple row, out HTuple column);
            HOperatorSet.CountObj(connected.Value, out HTuple count);

            _sink += area.Length + row.Length + column.Length + count.Length;
        }
        finally
        {
            // 异常路径兜底：若某个算子在创建句柄之前抛错，这里释放已经创建的裸对象。
            // 正常路径下三者均已置 null，不会重复释放。
            rawImage?.Dispose();
            rawRegion?.Dispose();
            rawConnected?.Dispose();
        }
    }

    private static BenchmarkResult Measure(string name, Options options, Action<int, int> body)
    {
        ForceFullGc();
        long baseline = CurrentProcess.PrivateMemorySize64;
        int gc0 = GC.CollectionCount(0);
        int gc1 = GC.CollectionCount(1);
        int gc2 = GC.CollectionCount(2);

        int sampleEvery = options.SampleEvery > 0
            ? options.SampleEvery
            : Math.Max(1, options.Iterations / 20);

        long peak = baseline;
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < options.Iterations; i++)
        {
            body(options.Width, options.Height);

            if ((i + 1) % sampleEvery == 0 || i == options.Iterations - 1)
            {
                CurrentProcess.Refresh();
                long current = CurrentProcess.PrivateMemorySize64;
                if (current > peak)
                {
                    peak = current;
                }
            }
        }
        stopwatch.Stop();

        int gc0After = GC.CollectionCount(0);
        int gc1After = GC.CollectionCount(1);
        int gc2After = GC.CollectionCount(2);

        CurrentProcess.Refresh();
        long end = CurrentProcess.PrivateMemorySize64;

        // 观察强制 GC 后能回收多少；Direct 组的差值体现"靠终结器兜底"的代价。
        ForceFullGc();
        long afterGc = CurrentProcess.PrivateMemorySize64;

        return new BenchmarkResult
        {
            Name = name,
            Iterations = options.Iterations,
            TotalMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            BaselineBytes = baseline,
            EndBytes = end,
            PeakBytes = Math.Max(peak, end),
            AfterGcBytes = afterGc,
            Gc0 = gc0After - gc0,
            Gc1 = gc1After - gc1,
            Gc2 = gc2After - gc2
        };
    }

    private static void ForceFullGc()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        CurrentProcess.Refresh();
    }

    private static void PrintReport(IReadOnlyDictionary<string, BenchmarkResult> results, string[] order, Options options)
    {
        Console.WriteLine("| 组       | 迭代 | 总耗时(ms) | 平均(us/次) | 起始(MB) | 结束(MB) | 增量(MB) | 峰值增量(MB) | GC后增量(MB) | GC 0/1/2 |");
        Console.WriteLine("|----------|-----:|-----------:|------------:|---------:|---------:|---------:|-------------:|-------------:|----------|");

        foreach (string name in new[] { "Direct", "Handle" })
        {
            if (!results.TryGetValue(name, out BenchmarkResult r))
            {
                continue;
            }

            Console.WriteLine($"| {r.Name,-8} | {r.Iterations,5} | {r.TotalMilliseconds,10:F1} | {r.AverageMicroseconds,11:F1} | " +
                              $"{ToMiB(r.BaselineBytes),8:F1} | {ToMiB(r.EndBytes),8:F1} | {ToMiB(r.DeltaBytes),8:F1} | " +
                              $"{ToMiB(r.PeakDeltaBytes),12:F1} | {ToMiB(r.AfterGcDeltaBytes),12:F1} | {r.Gc0}/{r.Gc1}/{r.Gc2} |");
        }

        Console.WriteLine();
        Console.WriteLine("说明:");
        Console.WriteLine("  - 内存指标取 PrivateMemorySize64（私有字节），能反映 Halcon 在非托管堆上的分配。");
        Console.WriteLine("  - 执行顺序: " + string.Join(" -> ", order) + "（避免 Direct 的残留影响 Handle 基线）。");

        if (results.TryGetValue("Direct", out BenchmarkResult direct) && results.TryGetValue("Handle", out BenchmarkResult handle))
        {
            Console.WriteLine($"  - 耗时比 Direct/Handle: {Ratio(direct.TotalMilliseconds, handle.TotalMilliseconds)}");
            Console.WriteLine($"  - 峰值内存增量比 Direct/Handle: {Ratio(direct.PeakDeltaBytes, handle.PeakDeltaBytes)}");
            Console.WriteLine($"  - Handle 组结束增量 {FormatMiB(handle.DeltaBytes)}，Direct 组结束增量 {FormatMiB(direct.DeltaBytes)}。");
        }

        Console.WriteLine("  - 重复运行请加 -c Release；需要绝对隔离时可分别执行 --group handle / --group direct。");
        Console.WriteLine($"  - 本次采样间隔: {(options.SampleEvery > 0 ? options.SampleEvery.ToString(CultureInfo.InvariantCulture) : "自动")}。");
    }

    private static string Ratio(double numerator, double denominator)
    {
        if (Math.Abs(denominator) < 0.001)
        {
            return "n/a（分母≈0）";
        }
        return (numerator / denominator).ToString("F2", CultureInfo.InvariantCulture) + "x";
    }

    private static double ToMiB(long bytes) => bytes / (double)MiB;

    private static string FormatMiB(long bytes) => ToMiB(bytes).ToString("F1", CultureInfo.InvariantCulture) + " MB";

    /// <summary>
    /// 用用户提供的 FakeResource 复现 HObjectHandleTests 的关键语义，
    /// 确保 benchmark 所依赖的"最后一个强句柄释放资源"等契约成立。
    /// </summary>
    private static bool VerifyHandleSemantics()
    {
        var checks = new (string Name, bool Passed)[]
        {
            ("Share 后仅最后一个句柄释放", CheckShareReleaseOnce()),
            ("Move 转移所有权并清空源句柄", CheckMove()),
            ("重复 Dispose 幂等", CheckRepeatedDispose()),
            ("释放后访问 Value 抛异常", CheckValueAfterDispose()),
            ("弱引用可在存活期提升", CheckWeakPromotion())
        };

        foreach ((string name, bool passed) in checks)
        {
            Console.WriteLine($"[自检] {(passed ? "PASS" : "FAIL")}  {name}");
        }
        return Array.TrueForAll(checks, c => c.Passed);
    }

    private static bool CheckShareReleaseOnce()
    {
        var resource = new FakeResource();
        HObjectHandle<FakeResource> first = HObjectHandle<FakeResource>.From(resource);
        HObjectHandle<FakeResource> second = first.Share();
        first.Dispose();
        bool ok = resource.DisposeCount == 0;
        second.Dispose();
        return ok && resource.DisposeCount == 1;
    }

    private static bool CheckMove()
    {
        var resource = new FakeResource();
        HObjectHandle<FakeResource> source = HObjectHandle<FakeResource>.From(resource);
        HObjectHandle<FakeResource> moved = source.Move();
        bool ok = source.IsEmpty && !moved.IsEmpty && ReferenceEquals(moved.Value, resource);
        source.Dispose();
        ok &= resource.DisposeCount == 0;
        moved.Dispose();
        return ok && resource.DisposeCount == 1;
    }

    private static bool CheckRepeatedDispose()
    {
        var resource = new FakeResource();
        HObjectHandle<FakeResource> handle = HObjectHandle<FakeResource>.From(resource);
        handle.Dispose();
        handle.Dispose();
        handle.Dispose();
        return resource.DisposeCount == 1;
    }

    private static bool CheckValueAfterDispose()
    {
        var resource = new FakeResource();
        HObjectHandle<FakeResource> handle = HObjectHandle<FakeResource>.From(resource);
        handle.Dispose();
        try
        {
            _ = handle.Value;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static bool CheckWeakPromotion()
    {
        var resource = new FakeResource();
        HObjectHandle<FakeResource> owner = HObjectHandle<FakeResource>.From(resource);
        HObjectWeakHandle<FakeResource> weak = owner.AsWeak();
        HObjectHandle<FakeResource> promoted = HObjectHandle<FakeResource>.Empty;
        bool ok = weak.IsAlive && weak.TryGetTarget(out promoted);
        ok &= promoted != null && ReferenceEquals(promoted.Value, resource);
        owner.Dispose();
        ok &= weak.IsAlive && resource.DisposeCount == 0;
        promoted?.Dispose();
        ok &= !weak.IsAlive && resource.DisposeCount == 1;
        ok &= !weak.TryGetTarget(out HObjectHandle<FakeResource> expired) && expired.IsEmpty;
        weak.Dispose();
        return ok;
    }

    private static Options ParseOptions(string[] args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            string key = token;
            string inlineValue = null;
            int equals = token.IndexOf('=');
            if (equals > 0)
            {
                key = token.Substring(0, equals);
                inlineValue = token.Substring(equals + 1);
            }

            switch (key)
            {
                case "-n":
                case "--iterations":
                    options.Iterations = ReadInt(args, ref i, key, inlineValue, 1);
                    break;
                case "-w":
                case "--warmup":
                    options.Warmup = ReadInt(args, ref i, key, inlineValue, 0);
                    break;
                case "--width":
                    options.Width = ReadInt(args, ref i, key, inlineValue, 1);
                    break;
                case "--height":
                    options.Height = ReadInt(args, ref i, key, inlineValue, 1);
                    break;
                case "--sample":
                    options.SampleEvery = ReadInt(args, ref i, key, inlineValue, 0);
                    break;
                case "-g":
                case "--group":
                    options.Group = ReadString(args, ref i, key, inlineValue).ToLowerInvariant();
                    if (options.Group is not ("both" or "direct" or "handle"))
                    {
                        throw new ArgumentException("--group 只能是 both / direct / handle");
                    }
                    break;
                default:
                    throw new ArgumentException("未知参数: " + token);
            }
        }
        return options;
    }

    private static int ReadInt(string[] args, ref int index, string key, string inlineValue, int minimum)
    {
        string text = inlineValue ?? ReadString(args, ref index, key, inlineValue);
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < minimum)
        {
            throw new ArgumentException($"{key} 需要 >= {minimum} 的整数，实际收到: {text}");
        }
        return value;
    }

    private static string ReadString(string[] args, ref int index, string key, string inlineValue)
    {
        if (inlineValue != null)
        {
            return inlineValue;
        }
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{key} 缺少值");
        }
        index++;
        return args[index];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法: dotnet run -c Release -- [选项]");
        Console.WriteLine();
        Console.WriteLine("选项:");
        Console.WriteLine("  -n, --iterations N   每组迭代次数（默认 2000）");
        Console.WriteLine("  -w, --warmup N       每组预热次数（默认 200）");
        Console.WriteLine("      --width N        图像宽度（默认 512）");
        Console.WriteLine("      --height N       图像高度（默认 512）");
        Console.WriteLine("      --sample N       内存采样间隔，0 表示自动取迭代数/20");
        Console.WriteLine("  -g, --group NAME     both | direct | handle（默认 both）");
        Console.WriteLine("  -h, --help           显示帮助");
        Console.WriteLine();
        Console.WriteLine("示例:");
        Console.WriteLine("  dotnet run -c Release -- --iterations 5000 --width 1024 --height 1024");
        Console.WriteLine("  dotnet run -c Release -- --group handle --iterations 10000");
        Console.WriteLine("  dotnet build -p:HalconDotNetPath=\"D:\\MVTec\\HALCON-24.05\\bin\\dotnet\\halcondotnetxl.dll\"");
    }
}
