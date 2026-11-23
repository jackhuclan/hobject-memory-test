# HObject 生命周期基准测试

本仓库用两个可独立运行的 .NET 控制台项目，演示并对比 Halcon `HObject` 的生命周期管理方式对**耗时**和**内存**的影响。两组测试都执行完全相同的 Halcon 算子流程：

```
GenImageConst → Threshold → Connection → AreaCenter / CountObj
```

唯一差异是资源的释放方式。核心结论是：`HObject` 的图像数据位于**非托管内存**，仅依赖 GC/终结器并不可靠；生产代码应使用确定性释放（`HObjectHandle<HObject>` 或 `try/finally` 手动 `Dispose`）。

## 目录结构

```
.
├── readme.md
├── docs/
│   └── HObjectLifetime-Beginners-Guide.md # HObjectHandle / WeakHandle / Holder 中文入门手册
├── dlls/                              # Halcon 24.5 XL 运行库（托管 + 原生）
│   ├── halcondotnetxl.dll
│   └── halconxl.dll
├── tools/
│   └── HObjectHolderGenerator/        # 根据 Halcon 元数据生成全部 out HObject 包装方法与接口声明
└── src/
    ├── HObjectLifetime.slnx           # 统一解决方案，包含下列 6 个项目
    ├── HObjectMemory.Lifetime/        # 自定义 HObjectHandle<T> 引用计数句柄类库
    │   ├── HObjectHolder.cs           # out HObject 算子的生命周期包装核心，实现 IHObjectHolder
    │   ├── IHObjectHolder.cs          # HObjectHolder 核心公共契约
    │   ├── HObjectHolder.Generated.cs # 生成的 598 个包装方法（勿手改）
    │   ├── IHObjectHolder.Generated.cs # 生成的 598 个接口声明（勿手改）
    │   └── Rectangle1TestData.cs      # 两个项目共用的 GenRectangle1 参数用例
    ├── HObjectMemory.Lifetime.Tests/  # MSTest 单元测试：类库全部类型与分支
    ├── HObjectMemoryTest/             # 项目一：低 GC 压力的自定义基准
    ├── HobjectBenchmark/              # 项目二：BenchmarkDotNet 基准
    │   ├── GenRectangle1Lifecycle.cs  # GenRectangle1 三种生命周期实现
    │   └── GenRectangle1Benchmarks.cs # GenRectangle1 独立基准入口
    └── HObjectHolderVerify/           # HObjectHolder 签名一致性与生命周期验证
```

> `HObjectHandle<T>` 是本仓库 `HObjectMemory.Lifetime` 项目中的**自定义引用计数句柄**，不是 Halcon 内置类型。它支持 `Share()` 共享、`Move()` 转移、`AsWeak()` 弱观察，并保证最后一个强句柄释放时底层资源只 `Dispose` 一次。

## HObjectHolder：out HObject 算子的生命周期包装

`HObjectHolder` 把 `HOperatorSet` 中所有以 `out HObject` 输出结果的方法包装为 `out HObjectWeakHandle<HObject>`。当前 HALCON 24.5 XL 下共 598 个方法、683 个 `out HObject` 参数（含 55 个多输出方法）；生成规则由 `tools/HObjectHolderGenerator` 从程序集元数据推导，**返回类型、参数名、参数顺序、其他参数的 out/ref 语义均与原方法一致**，唯一变化是输出参数类型。

公共契约由 `IHObjectHolder` 定义：核心生命周期成员位于 `IHObjectHolder.cs`，598 个算子包装声明位于 `IHObjectHolder.Generated.cs`，`HObjectHolder` 实现该接口。业务代码可使用 `IHObjectHolder holder = new HObjectHolder();` 依赖抽象；原有 `HObjectHolder` 具体类型声明与调用保持兼容。

面向初学者的三句柄使用手册见 [HObjectLifetime-Beginners-Guide.md](docs/HObjectLifetime-Beginners-Guide.md)。

核心成员与流程：

| 成员 | 作用 |
|---|---|
| `TrackedObjectCount` / `IsDisposed` | 监控容器持有的强引用锚点数量与容器状态 |
| `GetStrongRefCount(weak)` | 读取控制块当前强引用计数（锚点 + 已提升强句柄） |
| `Borrow(weak)` / `TryBorrow(weak, out h)` | 把弱句柄提升为强句柄；外部已 `Dispose` 的 HObject 无法提升 |
| `Release(weak)` | 提前释放单个锚点；仍有强句柄时资源延后释放 |
| `Dispose()` / 终结器 | 一次性释放全部锚点；可重复调用，最后一次强引用归零时自动 `Dispose` HObject |

生命周期：`算子 out → 原始 HObject → RefCountedOwner（强计数 1，容器锚点）→ 返回弱句柄`；需要传给普通算子时用 `Borrow` 提升为强句柄（计数 +1），用完 `Dispose`；容器 `Dispose` 释放锚点，若强计数归零则由 `RefCountedOwner.ReleaseStrong()` 确定性释放底层 HObject。弱句柄本身不延长生命周期，也不暴露已释放对象。

异常安全：算子调用失败时，包装方法释放已经写入的 `out HObject`、把输出置为空弱句柄，并原样重抛原始异常；多输出场景一次性提交锚点，避免部分登记。并发场景通过锁保护锚点集合，`HObjectHolderVerify` 用 64 路并行调用与重复 `Dispose` 做了覆盖。

```bash
# 重新生成包装方法与接口声明（HALCON 版本变化后执行；第三个参数可省略，默认生成同目录 IHObjectHolder.Generated.cs）
dotnet run --project tools/HObjectHolderGenerator -- dlls/halcondotnetxl.dll src/HObjectMemory.Lifetime/HObjectHolder.Generated.cs

# 验证签名一致性与生命周期边界
dotnet run --project src/HObjectHolderVerify/HObjectHolderVerify.csproj -c Release

# 运行类库单元测试（MSTest，含覆盖率收集）
dotnet test src/HObjectMemory.Lifetime.Tests/HObjectMemory.Lifetime.Tests.csproj -c Release --collect:"XPlat Code Coverage"
```

## 前置条件

- Windows x64，已安装 Halcon 24.5 XL 运行库及有效 License。
- 仓库自带 `dlls/halcondotnetxl.dll` 与 `dlls/halconxl.dll`；各可执行项目会将它们复制到输出目录。**不要删除 `dlls/`**，否则运行期会报 `DllNotFoundException`。
- .NET 10 SDK（当前项目 `TargetFramework` 为 `net10.0`）。
- 若要运行 `HobjectBenchmark`，还需安装 .NET 8 运行时：其基准配置显式指定 `CoreRuntime.Core80`（`HObjectLifecycleBenchmarks.cs` 中的 `HObjectBenchmarkConfig`）。

## 快速开始

```bash
cd E:\hobject-memory-test

# 1. 一次构建全部项目
dotnet build src/HObjectLifetime.slnx -c Release

# 2. 运行项目一：低 GC 压力对比
dotnet run --project src/HObjectMemoryTest/HObjectMemoryTest.csproj -c Release -- --iterations 1000 --width 512 --height 512

# 3. 运行项目一的 GenRectangle1 对比（独立入口）
dotnet run --project src/HObjectMemoryTest/HObjectMemoryTest.csproj -c Release -- --operator rectangle1 --iterations 500

# 4. 运行项目二：BenchmarkDotNet 基准
dotnet run --project src/HobjectBenchmark/HobjectBenchmark.csproj -c Release -- --list flat
dotnet run --project src/HobjectBenchmark/HobjectBenchmark.csproj -c Release -- --filter "*GenRectangle1Benchmarks*"
dotnet run --project src/HobjectBenchmark/HobjectBenchmark.csproj -c Release -- --filter "*"
```

> 构建完成后可在上述 `dotnet run` 命令中加 `--no-build` 跳过重复构建。

---

## 一、HObjectMemoryTest

### 基本功能与用途

一个手写计时/采样的对比程序，只有两组测试：

| 组名 | 释放方式 | 说明 |
|---|---|---|
| `Handle` | `HObjectHandle<HObject>.From(...)` + `using` | 引用计数归零时确定性 `Dispose`，且 `finally` 兜底异常路径 |
| `Direct` | 不调用 `Dispose` | 故意的错误写法，托管包装对象只能等 GC 终结器 |

启动时会先用 `FakeResource` 对句柄的 5 项关键语义做自检（共享仅末次释放、Move 转移、重复 Dispose 幂等、释放后访问抛异常、弱引用提升）；任一失败即中止，避免用错误的句柄实现得出错误结论。

### 适用场景

- 想快速看到**低 GC 压力**下的真实内存增长曲线。
- 想观察“强制 GC 后内存是否回落”，判断是终结器兜底还是真泄漏。
- 需要自由调整图像尺寸、迭代次数、采样间隔，做粗粒度压力复现。

### 运行步骤

```bash
# 默认配置：2000 次迭代、200 次预热、512×512、两组都跑
dotnet run --project src/HObjectMemoryTest/HObjectMemoryTest.csproj -c Release

# 自定义规模
dotnet run --project src/HObjectMemoryTest/HObjectMemoryTest.csproj -c Release -- --iterations 5000 --width 1024 --height 1024

# 只跑一组，做绝对隔离的对照
dotnet run --project src/HObjectMemoryTest/HObjectMemoryTest.csproj -c Release -- --group handle --iterations 10000
dotnet run --project src/HObjectMemoryTest/HObjectMemoryTest.csproj -c Release -- --group direct --iterations 10000

# 查看帮助
dotnet run --project src/HObjectMemoryTest/HObjectMemoryTest.csproj -c Release -- --help
```

### 常用参数

| 参数 | 默认值 | 含义 |
|---|---:|---|
| `-n`, `--iterations N` | 2000 | 每组迭代次数，`>=1` |
| `-w`, `--warmup N` | 200 | 每组预热次数，用于抵消 JIT 和 Halcon 首次初始化开销，`>=0` |
| `--width N` | 512 | 生成图像的宽度，`>=1` |
| `--height N` | 512 | 生成图像的高度，`>=1` |
| `--sample N` | 自动（迭代数/20） | 内存采样间隔；`0` 也表示自动 |
| `-g`, `--group NAME` | `both` | `both` / `direct` / `handle` |
| `-h`, `--help` | — | 打印帮助 |

参数支持 `--iterations 100` 和 `--iterations=100` 两种写法。

### 输出结果含义

控制台先打印环境信息与自检结果，最后输出一张表：

| 列 | 含义 |
|---|---|
| 组 | `Direct` 或 `Handle` |
| 迭代 | 本组实际迭代次数 |
| 总耗时(ms) | `Stopwatch` 测得的计算循环总时间，不含预热 |
| 平均(us/次) | 总耗时 / 迭代次数 |
| 起始(MB) | 强制 GC 后、计时开始前的 `PrivateMemorySize64` |
| 结束(MB) | 循环结束后进程的私有字节 |
| 增量(MB) | 结束 − 起始；能反映 Halcon 的非托管内存分配 |
| 峰值增量(MB) | 采样到的峰值 − 起始；按 `--sample` 间隔采样 |
| GC后增量(MB) | 循环结束后再次强制 GC 并等待终结器，再读私有字节 − 起始 |
| GC 0/1/2 | 测量区间内各代 GC 发生次数 |

表格下方还会给出：

- `耗时比 Direct/Handle`：大于 1 表示不释放组更慢；
- `峰值内存增量比 Direct/Handle`；
- `校验值 sink=...`：非 0 表示算子结果确实被计算，未被优化掉。

### 注意事项

- 默认执行顺序固定为 **Handle → Direct**：`Direct` 组不主动释放，若先跑会抬高后一组的基线。
- 内存指标取 `PrivateMemorySize64`，因此包含 Halcon 非托管分配，不要与 `GC.GetTotalMemory` 混淆。
- `GC后增量` 是判断“终结器兜底”还是“真实泄漏”的关键：若强制 GC 后仍不回落，说明原生资源仍被占用。
- 结果受机器、Halcon 版本、License 和后台负载影响；比较时应固定其余参数，多次运行取趋势而非单次绝对值。

### GenRectangle1 对比测试（独立入口）

通过 `--operator rectangle1` 启用，与默认图像算子链完全隔离，不传入该参数时原有测试不变：

```bash
dotnet run --project src/HObjectMemoryTest/HObjectMemoryTest.csproj -c Release -- --operator rectangle1 --iterations 500 --warmup 20

# 只跑一种生命周期策略
dotnet run --project src/HObjectMemoryTest/HObjectMemoryTest.csproj -c Release -- --operator rectangle1 --group handle --iterations 1000
```

每个用例执行 `GenRectangle1 → AreaCenter/CountObj`，`Handle` 组用 `HObjectHandle<HObject>` 包装并确定性释放，`Direct` 组不调用 `Dispose`。用例定义在 `HObjectMemory.Lifetime/Rectangle1TestData.cs`，两个项目共用：

| 用例名 | 坐标 (Row1,Col1)-(Row2,Col2) | 区域尺寸 | 覆盖点 |
|---|---|---:|---|
| `Normal_256x256` | (64,64)-(319,319) | 256×256 | 正常矩形 |
| `Small_1x1` | (10,10)-(10,10) | 1×1 | 两个维度均取边界相等值 |
| `Wide_512x8` | (10,10)-(17,521) | 512×8 | 宽扁尺寸 |
| `Tall_8x512` | (10,10)-(521,17) | 8×512 | 高窄尺寸 |
| `Large_1024x1024` | (0,0)-(1023,1023) | 1024×1024 | 从原点起始的大尺寸 |
| `Line_ZeroWidth` | (100,100)-(199,100) | 1×100 | `Column2 == Column1` 零宽退化 |
| `NegativeOrigin` | (-50,-20)-(20,30) | 51×71 | 负坐标起点 |

输出表格在原有列基础上增加 **用例**、**坐标**、**区域(宽x高)** 三列；每个用例独立预热并强制 GC，因此基线互不污染。表格下方逐用例给出 `耗时比 Direct/Handle` 与两组的内存增量，最后附 `sink` 校验值。

注意：Halcon 约束 `Row2 >= Row1` 且 `Column2 >= Column1`，上述用例均满足；不要传入 `Row2 < Row1` 或 `Column2 < Column1` 的参数，否则算子会抛异常。

---

## 二、HobjectBenchmark

### 基本功能与用途

基于 **BenchmarkDotNet 0.15.8** 的规范化基准项目，对比三种生命周期策略：

| 方法名 | 释放方式 | 角色 |
|---|---|---|
| `HandleDispose` | `HObjectHandle<HObject>` + `using` | **Baseline**，引用计数确定性释放 |
| `ManualDispose` | 裸 `HObject` + `try/finally` 逆序 `Dispose` | 参照组，量化手写管理的成本 |
| `DirectNoDispose` | 不主动释放 | 问题写法，观察 GC 兜底行为 |

基准配置位于 `HObjectLifecycleBenchmarks.cs`：

- `InProcessEmitToolchain`：诊断线程必须与基准运行在同一个进程内；
- `MemoryDiagnoser`：托管分配与 GC 次数；
- 自定义 `PrivateBytesDiagnoser`：每 5 ms 采样 `PrivateMemorySize64`，补充非托管内存视角；
- `Job.Default` + `CoreRuntime.Core80`，预热 3 轮、迭代 10 轮；
- 排序策略 `FastestToSlowest`。

### 适用场景

- 需要可重复、带统计意义（Mean / Error / StdDev / Ratio）的耗时对比。
- 同时关心托管分配（`Allocated`、`Gen0/Gen1`）和 Halcon 非托管内存。
- 想用 `--filter` 快速跑单个用例做验证。

### 运行步骤

```bash
# 列出全部基准用例
dotnet run --project src/HobjectBenchmark/HobjectBenchmark.csproj -c Release -- --list flat

# 完整运行（3 个方法 × 2 种迭代规模 = 6 个用例，约数分钟）
dotnet run --project src/HobjectBenchmark/HobjectBenchmark.csproj -c Release -- --filter "*"

# 只跑某一组
dotnet run --project src/HobjectBenchmark/HobjectBenchmark.csproj -c Release -- --filter "*HandleDispose*"

# 快速冒烟验证（覆盖配置中的 Job）
dotnet run --project src/HobjectBenchmark/HobjectBenchmark.csproj -c Release -- --filter "*" --job Dry
```

参数由 BenchmarkDotNet 的命令行解析器处理，常用项包括 `--filter`、`--list flat`、`--job`、`--runtimes` 等，可用 `--help` 查看完整列表。

**修改基准规模**：迭代次数由 `[Params(100, 1000)] public int Iterations` 控制；图像尺寸由 `ImageWidth` / `ImageHeight` 常量（当前 128×128）控制。修改后需重新构建。

### 输出结果含义

汇总表的关键列：

| 列 | 含义 |
|---|---|
| Mean / Error / StdDev | 单操作的平均耗时、误差与标准差 |
| Ratio | 相对 Baseline（`HandleDispose`）的耗时倍率 |
| Gen0 / Gen1 | 每 1000 次操作的 GC 次数（托管堆视角） |
| Allocated | 每次操作的托管分配字节数 |
| PrivateBytes增量(MB) | ActualRun 阶段结束时进程私有字节净增量，含 Halcon 非托管内存 |
| PrivateBytes峰值增量(MB) | ActualRun 阶段按 5 ms 采样到的私有字节峰值增量 |

`PrivateBytes增量(MB)` 恰为 0 时，BenchmarkDotNet 表格会显示为 `-`，表示没有可观测的原生内存增长，是确定性释放的理想结果。

结果同时写入当前工作目录下的 `BenchmarkDotNet.Artifacts/results/`，包含 Markdown、CSV、HTML 报告；完整控制台日志可用 shell 重定向保存。

### 注意事项

- `Allocated` 只统计**托管堆**，看不到 Halcon 图像所在的非托管内存；必须同时看 `PrivateBytes` 两列。
- BenchmarkDotNet 自身的分配会造成较高频的 GC（例如 Gen0 约 111 次/1000 操作）。`HObject` 继承 `HObjectBase`，**带终结器**，GC 触发时原生内存可能被回收，从而掩盖 `DirectNoDispose` 的泄漏。因此本项目的基准数据不能替代 `HObjectMemoryTest` 在低 GC 压力下的复现。
- 所有用例共享同一进程（`InProcessEmitToolchain`），先跑干净策略、后跑泄漏策略以减少污染；自定义筛选单个用例时结论可能不同。
- 配置中显式使用 `CoreRuntime.Core80`，目标机缺少 .NET 8 运行时会加载失败；如需改用其他运行时，请修改 `HObjectBenchmarkConfig` 后重建。
- 基准运行期间不要并行执行其他高负载任务，否则耗时与内存采样都会失真。

### GenRectangle1 性能基准（独立基准类）

`GenRectangle1Benchmarks` 是与 `HObjectLifecycleBenchmarks` 并列的独立基准类，复用同一个 `HObjectBenchmarkConfig`（InProcess + MemoryDiagnoser + PrivateBytesDiagnoser），通过用例名参数覆盖与上表相同的 7 组矩形数据：

```bash
# 只跑新增的 GenRectangle1 基准（3 个方法 × 7 个用例 = 21 个用例）
dotnet run --project src/HobjectBenchmark/HobjectBenchmark.csproj -c Release -- --filter "*GenRectangle1Benchmarks*"

# 只跑其中一个方法或某一个用例
dotnet run --project src/HobjectBenchmark/HobjectBenchmark.csproj -c Release -- --filter "*GenRectangle1Benchmarks.HandleDispose*"

# 只跑某个参数用例（用例名会进入 BenchmarkDotNet 的用例标识，可被 --filter 匹配）
dotnet run --project src/HobjectBenchmark/HobjectBenchmark.csproj -c Release -- --filter "*Small_1x1*"
```

- `CaseName`：由 `[ParamsSource]` 从 `Rectangle1TestData.Cases` 自动生成，汇总表中直接显示用例名。
- `Iterations`：由 `[Params(200)]` 控制每次调用内部的 `GenRectangle1` 次数；修改后需重新构建。
- 汇总表在原有列之外增加 `CaseName` 与 `Iterations`，性能结论按用例分别查看，不要跨尺寸直接比较 Mean。
- 命令行的 `--job Dry` 会在配置自带的 Job 之外**追加**一个 Job，因此用例数会翻倍（21 → 42），仅适合快速冒烟，不宜当作正式结果。

---

## 列表场景覆盖

两个项目的**现有生命周期方法内部**已补充列表创建与校验，没有新增任何入口方法：

- `List<HObject>`：在 `Direct` / `ManualDispose` 方法中创建，图像链 3 个元素（`image`、`region`、`connected`），矩形链 1 个元素（`rectangle`）。
- `List<HObjectHandle<HObject>>`：在 `HandleDispose` 方法中创建，元素为对应的引用计数句柄。
- 校验内容：列表非 `null`、`Count` 符合预期、每个元素类型正确；句柄列表还检查 `Value` 为 `HObject`。校验失败立即抛出 `InvalidOperationException`。
- 列表 `Count` 计入 `sink`，避免校验逻辑被优化掉。

这些逻辑随原有入口和基准一起执行，不产生独立命令；因为增加了少量托管分配，绝对耗时/内存数值会与未加列表校验时略有差异，但各策略之间的对比口径保持一致。

## 如何选择

| 需求 | 推荐项目 |
|---|---|
| 快速重现产线低 GC 场景下的内存持续增长 | `HObjectMemoryTest` |
| 观察 GC 前后内存变化、验证终结器是否兜底 | `HObjectMemoryTest` |
| 获取带统计意义的性能对比与 Baseline 倍率 | `HobjectBenchmark` |
| 同时看托管分配与非托管内存增量 | `HobjectBenchmark` |
| 验证 `HObjectHandle<T>` 自身语义是否正确 | `HObjectMemoryTest`（启动自检） |
| 单独验证 `GenRectangle1` 的对象创建/释放与内存表现 | `HObjectMemoryTest --operator rectangle1` |
| 获取 `GenRectangle1` 各尺寸与边界参数的性能数据 | `HobjectBenchmark` 的 `GenRectangle1Benchmarks` |

## 实践建议

1. 生产代码不要依赖终结器释放 Halcon 对象；GC 触发时机不可控，低分配压力阶段可能长时间不触发。
2. 优先使用 `HObjectHandle<HObject>` + `using`；简单线性脚本可用 `try/finally` 手动 `Dispose`，但必须登记每个中间对象。
3. 排查内存问题时，先用 `HObjectMemoryTest --group direct` 复现增长，再用 `HobjectBenchmark` 量化性能差异；针对 `GenRectangle1` 这类具体算子，改用 `--operator rectangle1` 与 `GenRectangle1Benchmarks` 做同口径对比。
4. 任何结论都要在目标 Halcon 版本、图像尺寸和部署环境上重新验证。
