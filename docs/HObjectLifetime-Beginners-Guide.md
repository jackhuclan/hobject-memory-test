# HObject 生命周期句柄入门手册

适用对象：第一次使用本仓库 `HObjectMemory.Lifetime` 的开发者。  
目标：搞清楚 **什么时候用哪种句柄、怎么写、为什么这样写**。本文不展开引用计数控制块、生成器等内部实现。

文中的三个主角：

| 类型 | 一句话定位 |
|---|---|
| `HObjectHandle<T>` | **强句柄**：谁持有它，谁就参与决定底层对象什么时候释放。 |
| `HObjectWeakHandle<T>` | **弱句柄**：只观察对象是否还在，不决定对象寿命。 |
| `HObjectHolder` | **批量托管容器**：调用 Halcon 算子时把 `out HObject` 变成 `out HObjectWeakHandle<HObject>`，由容器统一持有资源寿命。 |

如果你只记一句话：

> **自己创建一个裸 `HObject`，用 `HObjectHandle<T>`；只想看一眼不负责寿命，用 `HObjectWeakHandle<T>`；大量调用 `HOperatorSet` 且输出是 `out HObject`，优先用 `HObjectHolder`。**

---

## 0. 阅读约定

示例默认包含：

```csharp
using HalconDotNet;
using HObjectMemory.Lifetime;
```

`HObjectHolder` 实现 `IHObjectHolder`，推荐把变量声明为接口：

```csharp
using IHObjectHolder holder = new HObjectHolder();
```

直接写 `using HObjectHolder holder = new HObjectHolder();` 也完全兼容。

---

## 1. 核心概念与适用目标

### 1.1 `HObjectHandle<T>`：强句柄，负责“拥有”

`HObjectHandle<T>` 是资源的**所有权句柄**。它会延长底层资源的生命周期：只要还有一个有效强句柄存在，底层对象就不会被释放；最后一个强句柄释放时，底层对象被释放。

常见用途：

- 你通过 `HOperatorSet.GenImageConst`、`ReadImage` 等原始算子拿到了一个裸 `HObject`。
- 这个对象需要在多个函数、多个阶段之间传递。
- 你希望用 `using` 明确控制释放时机，而不是依赖 GC。

它不适合的场景：

- 只是缓存一个“如果还在就用”的引用，不希望影响释放时机。
- 只是 UI 上观察状态，不应该阻止资源释放。

核心成员：

| 成员 | 用途 |
|---|---|
| `HObjectHandle<T>.From(value)` | 接管一个现有资源，创建强句柄。 |
| `Value` | 取出裸 `HObject`，传给原始 Halcon 算子。对象已释放时抛 `ObjectDisposedException`。 |
| `TryGetValue(out value)` | 安全地尝试取出裸对象，失败返回 `false`。 |
| `Share()` | 复制一个强句柄，多个持有者共同决定寿命。 |
| `Move()` | 把所有权转给新句柄，原句柄立即变空。 |
| `AsWeak()` | 生成一个不延长寿命的弱观察者。 |
| `IsEmpty` | 句柄是否不再持有有效资源。 |
| `Dispose()` | 释放当前强引用；可重复调用。 |

### 1.2 `HObjectWeakHandle<T>`：弱句柄，负责“观察”

`HObjectWeakHandle<T>` 不延长资源寿命。它只回答两个问题：

1. 对象还在吗？——`IsAlive`
2. 如果还在，能否临时升级成强句柄来用？——`TryGetTarget`

常见用途：

- 缓存/观察者：对象还活着就使用，已经释放就跳过。
- UI 状态显示：不希望因为显示引用而阻止资源释放。
- 与 `HObjectHolder` 配合：Holder 返回弱句柄，调用方按需临时借用。

核心成员：

| 成员 | 用途 |
|---|---|
| `HObjectWeakHandle<T>.Empty` | 空弱句柄，所有操作安全。 |
| `IsAlive` | 被观察的对象是否仍然存活。 |
| `TryGetTarget(out handle)` | 尝试提升为强句柄；失败时返回空强句柄和 `false`。 |
| `Dispose()` | 释放“观察者”本身，**不会释放底层对象**。 |

### 1.3 `HObjectHolder`：容器，负责“批量托管”

`HObjectHolder` 面向大量 Halcon 算子调用。它包装了 `HOperatorSet` 中以 `out HObject` 输出结果的方法：

```csharp
// 原始 Halcon API
HOperatorSet.Threshold(image, out HObject region, minGray, maxGray);

// Holder 包装后
holder.Threshold(image, out HObjectWeakHandle<HObject> region, minGray, maxGray);
```

返回类型、参数名、参数顺序都与原算子一致，唯一变化是：

```text
out HObject  →  out HObjectWeakHandle<HObject>
```

这样做的好处：

- 调用方拿到的是弱句柄，不会因为忘记释放某个输出而直接泄漏。
- Holder 为输出对象保留一个“锚点”强引用，对象在容器生命周期内保持有效。
- 容器释放或显式 `Release` 后，对象会在最后一个强引用释放时被确定性释放。
- 算子失败时，包装方法会清理已经写出的对象，并把 out 参数置为空弱句柄。

常用成员：

| 成员 | 用途 |
|---|---|
| `Borrow(weak)` | 把弱句柄提升为强句柄；失败抛异常。调用方负责 `Dispose()`。 |
| `TryBorrow(weak, out handle)` | 尝试提升；失败返回 `false`，适合分支处理。 |
| `Release(weak)` | 提前释放某一个输出对象的容器锚点。 |
| `Dispose()` | 释放容器内所有锚点；可重复调用。 |
| `TrackedObjectCount` | 当前容器持有多少个锚点，主要用于诊断。 |
| `IsDisposed` | 容器是否已释放。 |
| `GetStrongRefCount(weak)` | 诊断当前还有多少强引用，主要用于排查。 |

---

## 2. 基本使用流程

### 2.1 `HObjectHandle<T>`：接管 → 使用 → 释放

```csharp
// 1. 原始算子产出裸 HObject
HOperatorSet.GenImageConst(out HObject raw, "byte", 64, 64);

// 2. 交给强句柄接管
using HObjectHandle<HObject> image = HObjectHandle<HObject>.From(raw);

// 3. 需要传给原始 Halcon 算子时，从 Value 取出裸对象
HOperatorSet.AreaCenter(image.Value, out HTuple area, out HTuple row, out HTuple col);

// 4. using 结束自动释放；不要再手动 raw.Dispose()
```

为什么这样写：

- `From(raw)` 表示资源所有权转交给句柄；之后由句柄负责释放。
- `Value` 只是“借用”裸对象给一次算子调用，不要把 `image.Value` 存成长期字段。
- `using` 把释放时机绑定到作用域，异常路径也能释放。

如果需要多个模块同时使用：

```csharp
using HObjectHandle<HObject> owner = HObjectHandle<HObject>.From(raw);

using HObjectHandle<HObject> worker1 = owner.Share();
using HObjectHandle<HObject> worker2 = owner.Share();

// worker1、worker2 都释放后，只要 owner 还在，对象就还活着。
```

`Share()` 是“增加一个拥有者”；`Move()` 是“换一个拥有者，原句柄变空”。

### 2.2 `HObjectWeakHandle<T>`：观察 → 尝试提升 → 使用

弱句柄通常不直接创建，而是由强句柄产生：

```csharp
using HObjectHandle<HObject> image = HObjectHandle<HObject>.From(raw);

HObjectWeakHandle<HObject> observer = image.AsWeak();

if (observer.TryGetTarget(out HObjectHandle<HObject> target))
{
    using (target)
    {
        HOperatorSet.AreaCenter(target.Value, out HTuple area, out HTuple row, out HTuple col);
    }
}
else
{
    // 对象已经释放，不能继续使用
}

observer.Dispose(); // 只释放观察者，不释放 image
```

为什么这样写：

- `observer` 不阻止 `image` 被释放。
- `TryGetTarget` 成功后得到的是强句柄，使用期间对象不会突然消失。
- 提升得到的强句柄要按所有权处理，用完立即 `Dispose`。
- 提升失败时不会拿到已释放的裸对象，避免访问非法资源。

### 2.3 `HObjectHolder`：创建容器 → 调用包装方法 → 借用 → 释放容器

```csharp
using IHObjectHolder holder = new HObjectHolder();

// 1. 直接调用包装方法，输出是弱句柄
holder.GenImageConst(out HObjectWeakHandle<HObject> imageWeak, "byte", 128, 128);

// 2. 要把弱句柄传给普通 Halcon 算子时，先借用为强句柄
using (HObjectHandle<HObject> image = holder.Borrow(imageWeak))
{
    holder.Threshold(image.Value, out HObjectWeakHandle<HObject> regionWeak, 128, 255);

    using (HObjectHandle<HObject> region = holder.Borrow(regionWeak))
    {
        holder.Connection(region.Value, out HObjectWeakHandle<HObject> connectedWeak);

        using (HObjectHandle<HObject> connected = holder.Borrow(connectedWeak))
        {
            HOperatorSet.CountObj(connected.Value, out HTuple count);
        }
    }
}

// 3. holder  Dispose 后，所有未借出的输出对象都会释放
```

为什么这样写：

- 包装方法负责登记输出对象，调用方不用给每个 `out HObject` 写 `try/finally`。
- `Borrow` 得到的强句柄使用完立即 `Dispose`，对象会在最后一个强引用释放时释放。
- Holder 管的是“容器内所有输出”的生命周期；借用句柄管的是“当前使用片段”的生命周期。

---

## 3. 三者差异与选择依据

| 问题 | `HObjectHandle<T>` | `HObjectWeakHandle<T>` | `HObjectHolder` |
|---|---|---|---|
| 是否延长对象寿命 | 是 | 否 | 是，通过容器锚点 |
| 谁负责释放 | 最后一个强句柄 | 不负责释放资源 | 容器释放或 `Release` |
| 能否取出裸 `HObject` | 可以，`Value` / `TryGetValue` | 不直接取；先提升为强句柄 | 不直接取；先 `Borrow` / `TryBorrow` |
| 典型入口 | `From` / `Share` / `Move` | `AsWeak` / Holder 输出 | 生成的算子包装方法 |
| 适合 | 自己管理一个资源的拥有权 | 观察、缓存、UI 状态 | 大量 `out HObject` 算子调用 |
| 不适合 | 只观察不拥有 | 作为唯一生命周期管理器 | 输入型算子、非 `out HObject` 场景 |

选择流程：

1. **输出是 `HObject` 吗？**  
   是 → 优先 `HObjectHolder` 的包装方法。
2. **只是想缓存一个引用，不希望阻止释放吗？**  
   是 → `HObjectWeakHandle<T>`。
3. **拿到了裸 `HObject`，需要自己管控释放吗？**  
   是 → `HObjectHandle<T>`。
4. **输出对象需要活过 Holder 的作用域吗？**  
   在 Holder 释放前用 `Borrow` 取得强句柄并保留，最后释放该强句柄。

---

## 4. 典型场景

### 场景 1：读取图像，在多个函数间传递

```csharp
HOperatorSet.ReadImage(out HObject raw, "part.bmp");
using HObjectHandle<HObject> image = HObjectHandle<HObject>.From(raw);

Process(image);
Inspect(image);

static void Process(HObjectHandle<HObject> image)
{
    using HObjectHandle<HObject> local = image.Share();
    HOperatorSet.AreaCenter(local.Value, out HTuple area, out HTuple row, out HTuple col);
}
```

要点：

- 函数签名接收 `HObjectHandle<HObject>`，不要接收裸 `HObject`。
- 函数内部若需要长期持有，用 `Share()`；只是临时使用，可以直接用传入句柄的 `Value`。
- 不要写 `raw.Dispose()`，否则强句柄还在但底层对象已被外部提前释放。

### 场景 2：只观察，不参与释放

```csharp
using HObjectHandle<HObject> image = HObjectHandle<HObject>.From(raw);
HObjectWeakHandle<HObject> observer = image.AsWeak();

// 某个稍后的时机
if (observer.TryGetTarget(out HObjectHandle<HObject> target))
{
    using (target)
    {
        HOperatorSet.GetImageSize(target.Value, out HTuple width, out HTuple height);
    }
}
else
{
    // 图像已经释放，跳过处理
}
```

要点：

- `IsAlive` 为 `false` 时不要继续调用算子。
- `TryGetTarget` 成功后必须把返回的强句柄当作Ownership处理，用完 `Dispose`。
- `observer.Dispose()` 不会释放图像，只释放观察关系。

### 场景 3：用 Holder 生成图像并处理

```csharp
using IHObjectHolder holder = new HObjectHolder();

holder.GenImageConst(out HObjectWeakHandle<HObject> imageWeak, "byte", 256, 256);

using (HObjectHandle<HObject> image = holder.Borrow(imageWeak))
{
    HOperatorSet.GetImageSize(image.Value, out HTuple width, out HTuple height);

    holder.Threshold(image.Value, out HObjectWeakHandle<HObject> regionWeak, 100, 200);
}
```

要点：

- `holder.GenImageConst` 的输出已经由 Holder 托管。
- `holder.Threshold` 的第一个参数仍是裸 `HObject`，所以从强句柄的 `Value` 取。
- `regionWeak` 也是由 Holder 托管，后续需要时再 `Borrow`。

### 场景 4：一次算子输出多个对象

```csharp
using IHObjectHolder holder = new HObjectHolder();

holder.GenImageConst(out HObjectWeakHandle<HObject> imageWeak, "byte", 64, 64);

using (HObjectHandle<HObject> image = holder.Borrow(imageWeak))
{
    holder.Watersheds(image.Value,
        out HObjectWeakHandle<HObject> basins,
        out HObjectWeakHandle<HObject> watersheds);

    // 两个输出都有效；此时可用 holder.TrackedObjectCount 观察锚点数量

    // 只提前释放其中一个
    holder.Release(basins);
}
```

要点：

- 每个 `out HObjectWeakHandle<HObject>` 都是独立对象。
- `Release(basins)` 只释放 `basins` 的容器锚点，不影响 `watersheds`。
- 如果此前已经 `Borrow(basins)` 且未释放强句柄，`Release` 后对象仍会存活到强句柄释放。

### 场景 5：使用可能失败的算子

```csharp
using IHObjectHolder holder = new HObjectHolder();
holder.GenImageConst(out HObjectWeakHandle<HObject> imageWeak, "byte", 64, 64);

using HObjectHandle<HObject> image = holder.Borrow(imageWeak);

try
{
    holder.Threshold(image.Value, out HObjectWeakHandle<HObject> regionWeak, "bad_min_gray", 128);
}
catch (HalconException)
{
    // 包装方法已经清理失败算子的输出；region 变量若声明在外，会是空弱句柄。
}
```

要点：

- 使用 Holder 包装方法时，不需要自己给每个输出写清理逻辑。
- 如果直接调用 `HOperatorSet` 得到裸 `HObject`，则需要自己写 `try/finally` 或马上交给 `HObjectHandle<T>.From`。

### 场景 6：让输出活过 Holder 作用域

```csharp
HObjectHandle<HObject> keepAlive;

using (IHObjectHolder holder = new HObjectHolder())
{
    holder.GenImageConst(out HObjectWeakHandle<HObject> weak, "byte", 32, 32);
    keepAlive = holder.Borrow(weak);
}

// holder 已经释放，但 keepAlive 仍持有强引用，对象还活着
HOperatorSet.GetImageSize(keepAlive.Value, out HTuple width, out HTuple height);

keepAlive.Dispose(); // 此刻对象才释放
```

要点：

- Holder 释放不等于所有对象立即释放，已借出的强句柄仍然有效。
- 需要跨作用域存活时，必须在 Holder 释放前 `Borrow`。
- 不要再把这个强句柄偷偷转回弱句柄后长期保存；弱句柄不保命。

---

## 5. 常见误区与注意事项

### 误区 1：把弱句柄当成“轻量版强句柄”

弱句柄不延长寿命。只看弱句柄、不持有任何强句柄时，对象随时可能已经释放。

正确做法：要用对象时先 `Borrow` / `TryGetTarget`，并在 `using` 中使用强句柄。

### 误区 2：认为 `weak.Dispose()` 会释放图像

不会。`HObjectWeakHandle<T>.Dispose()` 只释放观察者本身。释放资源的是最后一个强句柄或 Holder 锚点。

### 误区 3：把 `Value` 取出后长期保存

`Value` 返回裸对象，绕过生命周期管理。正确模式是“随用随取”：

```csharp
using HObjectHandle<HObject> image = owner.Share();
HOperatorSet.SomeOperator(image.Value, ...);
```

不要写：

```csharp
HObject leaked = image.Value; // 句柄释放后 leaked 就成了悬空引用
```

### 误区 4：同时用强句柄和裸对象手动释放

```csharp
using HObjectHandle<HObject> image = HObjectHandle<HObject>.From(raw);
raw.Dispose(); // 错误：外部提前释放，弱句柄会失效
```

接管之后，只通过句柄和容器管理释放。

### 误区 5：忘记释放借出的强句柄

`Borrow` 返回的强句柄会增加引用计数。忘记 `Dispose` 会导致对象比预期活得更久。

```csharp
HObjectHandle<HObject> borrowed = holder.Borrow(weak);
// ... 使用 ...
borrowed.Dispose();
```

更推荐：

```csharp
using HObjectHandle<HObject> borrowed = holder.Borrow(weak);
```

### 误区 6：容器释放后继续调用包装方法

`holder.Dispose()` 之后再调用任何包装方法会抛 `ObjectDisposedException`。这是有意设计，用来暴露生命周期错误。

### 误区 7：认为 `IsEmpty` 表示 HALCON 空对象

`HObjectHandle<T>.IsEmpty` 表示“这个句柄不持有有效资源”，不是指 HALCON 里的空图像对象。判断 HALCON 对象内容请使用对应的 Halcon API。

### 误区 8：混淆 `Share` 和 `Move`

- `Share()`：复制所有权，原句柄继续有效。
- `Move()`：转移所有权，原句柄变空。

如果只是想把句柄传给另一个函数，而原作用域还要用，选择 `Share()`。

### 并发注意事项

- `HObjectHolder` 内部会保护锚点集合，可以并发调用不同包装方法。
- 每个线程最好各自 `Borrow` 出自己的强句柄，不要多个线程共享同一个强句柄并同时释放。
- Halcon 对象本身的并发使用仍需遵守 Halcon 的约束。

---

## 6. 排查思路

### 症状：`Value` 抛 `ObjectDisposedException`

可能原因：

- 强句柄已经 `Dispose`。
- 传入的是 `Move()` 后的空句柄。
- 底层对象被外部提前 `Dispose`。

处理：

1. 检查 `handle.IsEmpty`。
2. 如果是 Holder 输出，检查 `weak.IsAlive`。
3. 若弱句柄仍存活，重新 `Borrow` / `TryGetTarget`。
4. 若弱句柄已失效，回到更早的生命周期所有者，检查 Holder 是否已释放或 `Release`。

### 症状：`Borrow` 失败或 `TryBorrow` 返回 `false`

可能原因：

- Holder 已 `Dispose`。
- 对应锚点已被 `Release`。
- 底层对象被外部释放。
- 传错弱句柄，比如来自另一个 Holder。

处理：

1. 检查 `holder.IsDisposed`。
2. 检查 `weak.IsAlive`。
3. 检查是否调用过 `holder.Release(weak)`。
4. 检查是否把同一个弱句柄跨容器混用。

### 症状：`TrackedObjectCount` 没有按预期下降

可能原因：

- Holder 还没 `Dispose`。
- 只调用了 `Release` 某个对象，其他对象仍被托管。
- 之前 `Borrow` 的强句柄还没有释放。

处理：

```csharp
int count = holder.TrackedObjectCount;
int refs = holder.GetStrongRefCount(weak);
```

`GetStrongRefCount` 是诊断工具，正常业务逻辑不要依赖精确数值。

### 症状：对象没有释放，内存不降

排查顺序：

1. Holder 是否还在 `using` 作用域内？
2. 是否忘记 `holder.Dispose()`？
3. 是否有借出的强句柄未释放？
4. 是否把裸 `HObject` 存到了字段、集合或闭包里？
5. 是否调用了 `HObjectWeakHandle.Dispose()` 却误以为资源已释放？

### 症状：算子异常后输出对象状态不明确

- 用 Holder 包装方法：异常路径会自动清理输出，out 参数为空弱句柄。
- 用原始 `HOperatorSet`：自己补 `try/finally` 或立刻交给 `HObjectHandle<T>.From`。

---

## 7. 最后速查清单

写代码前问自己三件事：

1. **这个对象的寿命应该由谁决定？**  
   自己决定 → `HObjectHandle<T>`；容器决定 → `HObjectHolder`；不决定 → `HObjectWeakHandle<T>`。

2. **拿到的是裸 `HObject` 还是弱句柄？**  
   裸 `HObject` → `From` 接管；弱句柄 → `Borrow` / `TryGetTarget` 后再用。

3. **使用结束时谁负责释放？**  
   强句柄 → `Dispose`；Holder → `Dispose` 或 `Release`；弱句柄 → 不负责释放资源。

推荐默认写法：

```csharp
using IHObjectHolder holder = new HObjectHolder();

holder.ReadImage(out HObjectWeakHandle<HObject> imageWeak, "part.bmp");

if (holder.TryBorrow(imageWeak, out HObjectHandle<HObject> image))
{
    using (image)
    {
        HOperatorSet.GetImageSize(image.Value, out HTuple width, out HTuple height);
    }
}
```

牢记：**弱句柄负责“能不能用”，强句柄负责“用到什么时候”，Holder 负责“批量输出什么时候释放”。**
