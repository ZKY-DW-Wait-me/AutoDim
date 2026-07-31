namespace AutoDim.Config;

/// <summary>要生成的标注类别（可组合）。</summary>
[System.Flags]
public enum DimCategory
{
    None    = 0,
    Overall = 1,   // 总体外形尺寸
    Segment = 2,   // 轮廓直线段长度
    Holes   = 4,   // 孔/圆 直径与定位
    Angular = 8,   // 相邻边夹角
    All     = Overall | Segment | Holes | Angular
}

/// <summary>插件运行选项。Phase 1 只用到 Categories / LayerName / OverallGap，其余为后续阶段预留。</summary>
public sealed class AutoDimOptions
{
    /// <summary>启用哪些标注类别。</summary>
    public DimCategory Categories { get; set; } = DimCategory.All;

    /// <summary>标注所在图层名（不存在则自动创建）。</summary>
    public string LayerName { get; set; } = "ADIM";

    /// <summary>总体尺寸线离开包围盒的距离；&lt;=0 表示按包围盒尺寸自动推算。</summary>
    public double OverallGap { get; set; } = 0.0;

    /// <summary>轮廓分段尺寸的外偏移距离；&lt;=0 表示自动推算（Phase 3 使用）。</summary>
    public double SegmentGap { get; set; } = 0.0;

    /// <summary>是否跳过直角的角度标注（Phase 4 使用）。</summary>
    public bool SkipRightAngles { get; set; } = true;
}
