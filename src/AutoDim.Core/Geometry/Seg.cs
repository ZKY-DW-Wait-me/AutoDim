namespace AutoDim.Core.Geometry;

/// <summary>
/// 带 bulge 的线段：bulge=0 表示直线段；非 0 表示圆弧弦（AutoCAD bulge 约定，
/// 正值为 A→B 逆时针）。圆弧不再采样成碎弦，以单段+bulge 完整保留。
/// </summary>
public readonly record struct Seg(Point2D A, Point2D B, double Bulge = 0);
