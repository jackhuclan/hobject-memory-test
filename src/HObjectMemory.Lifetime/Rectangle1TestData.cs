namespace HObjectMemory.Lifetime;

/// <summary>
/// GenRectangle1 的一组输入参数：左上角 (Row1, Column1) 与右下角 (Row2, Column2)。
/// Halcon 约束：Row2 &gt;= Row1 且 Column2 &gt;= Column1，等于时用于退化矩形边界。
/// </summary>
public sealed class Rectangle1Case
{
    public Rectangle1Case(string name, double row1, double column1, double row2, double column2)
    {
        Name = name;
        Row1 = row1;
        Column1 = column1;
        Row2 = row2;
        Column2 = column2;
    }

    public string Name { get; }

    public double Row1 { get; }

    public double Column1 { get; }

    public double Row2 { get; }

    public double Column2 { get; }

    /// <summary>区域宽度（像素数）：Column2 - Column1 + 1。</summary>
    public double Width => Column2 - Column1 + 1;

    /// <summary>区域高度（像素数）：Row2 - Row1 + 1。</summary>
    public double Height => Row2 - Row1 + 1;

    public override string ToString() => Name;
}

/// <summary>
/// 两个基准项目共用的矩形测试数据：覆盖正常矩形、不同尺寸与典型边界参数。
/// </summary>
public static class Rectangle1TestData
{
    public static readonly IReadOnlyList<Rectangle1Case> Cases = new[]
    {
        // 正常矩形：256 x 256
        new Rectangle1Case("Normal_256x256", 64, 64, 319, 319),

        // 最小尺寸 + 双边界相等：1 x 1 单点
        new Rectangle1Case("Small_1x1", 10, 10, 10, 10),

        // 宽扁矩形：8 行 x 512 列
        new Rectangle1Case("Wide_512x8", 10, 10, 17, 521),

        // 高窄矩形：512 行 x 8 列
        new Rectangle1Case("Tall_8x512", 10, 10, 521, 17),

        // 大尺寸矩形：1024 x 1024，从原点开始
        new Rectangle1Case("Large_1024x1024", 0, 0, 1023, 1023),

        // 零宽边界：Column2 == Column1，退化为一条竖线
        new Rectangle1Case("Line_ZeroWidth", 100, 100, 199, 100),

        // 负坐标起点：验证越界坐标不会导致算子异常
        new Rectangle1Case("NegativeOrigin", -50, -20, 20, 30)
    };
}
