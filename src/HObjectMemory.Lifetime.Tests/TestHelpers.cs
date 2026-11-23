using HalconDotNet;

namespace HObjectMemory.Lifetime.Tests;

/// <summary>
/// 单元测试共用的 HALCON 依赖隔离辅助。
/// 纯引用计数逻辑不需要 HALCON 运行库；只有 HObjectHolder 的 HObject 相关测试需要。
/// </summary>
internal static class TestHelpers
{
    public static bool HalconRuntimeAvailable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "halconxl.dll")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "halcondotnetxl.dll"));

    public static void RequireHalconRuntime()
    {
        if (!HalconRuntimeAvailable)
        {
            Assert.Inconclusive("当前输出目录缺少 halconxl.dll / halcondotnetxl.dll，跳过 HALCON 相关测试。");
        }
    }

    /// <summary>创建一个已初始化但不占用图像数据的空 HObject。</summary>
    public static HObject CreateEmptyHObject()
    {
        RequireHalconRuntime();
        HObject value = new();
        value.GenEmptyObj();
        return value;
    }
}
