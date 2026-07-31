using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoDim.Cad;
using AutoDim.Config;

namespace AutoDim.Dimensioning;

/// <summary>各类别生成的标注数量汇总。</summary>
public struct DimResult
{
    public int Overall;   // 总体外形
    public int Segment;   // 轮廓直线段
    public int Arc;       // 轮廓圆弧段半径
    public int Circle;    // 孔/圆 直径
    public int Position;  // 孔/圆 定位坐标
    public int Angular;   // 相邻边夹角
    public bool SkipW;    // 总体宽是否被跳过(封闭链检测)
    public bool SkipH;    // 总体高是否被跳过

    public int Total => Overall + Segment + Arc + Circle + Position + Angular;
}

/// <summary>标注编排器：算一次包围盒/基准间距，按启用的类别依次调用各 Dimensioner（保证分层一致）。</summary>
internal static class DimensionEngine
{
    public static (bool skipW, bool skipH) LastSkip { get; private set; }

    /// <summary>选集中是否全是"任意倾斜多边形"(无水平/垂直直边，含 Polyline 和独立 Line)。
    /// 有一条水平/垂直边就返回 false(走正交路径)；没有任何直边也返回 false。</summary>
    private static bool IsArbitraryPolygonSet(Transaction tr, IReadOnlyList<ObjectId> ids)
    {
        bool any = false;
        bool hasHv = false;
        foreach (var id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is Polyline pl)
            {
                int n = pl.NumberOfVertices;
                for (int i = 0; i < n; i++)
                {
                    if (!pl.Closed && i == n - 1) break;
                    int j = (i + 1) % n;
                    if (System.Math.Abs(pl.GetBulgeAt(i)) > 1e-9) continue;
                    any = true;
                    Point2d a = pl.GetPoint2dAt(i), b = pl.GetPoint2dAt(j);
                    if (System.Math.Abs(a.X - b.X) < 1e-6 || System.Math.Abs(a.Y - b.Y) < 1e-6)
                        hasHv = true;
                }
            }
            else if (tr.GetObject(id, OpenMode.ForRead) is Line ln)
            {
                any = true;
                var a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                var b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                if (System.Math.Abs(a.X - b.X) < 1e-6 || System.Math.Abs(a.Y - b.Y) < 1e-6)
                    hasHv = true;
            }
        }
        return any && !hasHv;
    }

    public static DimResult Run(Database db, Transaction tr, BlockTableRecord space,
                                IReadOnlyList<ObjectId> ids, AutoDimOptions opt,
                                ObjectId dimStyleId, ObjectId layerId, Extents3d? ext)
    {
        var r = new DimResult();

        double baseGap = ext != null ? GeometryUtils.AutoGap(ext.Value) : 10.0;

        // 任意倾斜多边形(无水平/垂直直边)：外包框总体尺寸没意义(挂空)、直角坐标投影没基准。
        // 改用模式 B：对齐边长 + 关键夹角。检测到就走任意多边形路径，跳过总体/常规分段。
        bool arbitrary = IsArbitraryPolygonSet(tr, ids);
        if (arbitrary)
        {
            if (opt.Categories.HasFlag(DimCategory.Segment))
            {
                var (e, ang) = ArbitraryPolygonDimensioner.Annotate(db, tr, space, ids, dimStyleId, layerId, baseGap);
                r.Segment = e;
                r.Angular = ang;
            }
            if (opt.Categories.HasFlag(DimCategory.Holes))
            {
                var (dia, pos) = CircleDimensioner.Annotate(db, tr, space, ids, ext, dimStyleId, layerId, baseGap);
                r.Circle = dia; r.Position = pos;
            }
            return r;
        }

        // 先看轮廓里有没有跟总体宽/高重合的整条底/侧边——重合就让总体跳过该方向，
        // 否则会标两个一样的尺寸(如 100×60 矩形：底边分段 100 + 总体宽 100)。
        bool skipW = false, skipH = false;
        if (ext != null && opt.Categories.HasFlag(DimCategory.Overall) && opt.Categories.HasFlag(DimCategory.Segment))
        {
            (skipW, skipH) = GeometryUtils.DetectOutermostEdges(tr, ids, ext.Value);
        }
        LastSkip = (skipW, skipH);
        r.SkipW = skipW; r.SkipH = skipH;

        if (opt.Categories.HasFlag(DimCategory.Overall))
            r.Overall = OverallDimensioner.Annotate(db, tr, space, ext, dimStyleId, layerId, baseGap, skipW, skipH);

        if (opt.Categories.HasFlag(DimCategory.Holes))
        {
            var (dia, pos) = CircleDimensioner.Annotate(db, tr, space, ids, ext, dimStyleId, layerId, baseGap);
            r.Circle = dia;
            r.Position = pos;
        }

        if (opt.Categories.HasFlag(DimCategory.Segment))
        {
            var (s, a) = OutlineDimensioner.Annotate(db, tr, space, ids, dimStyleId, layerId, baseGap, ext);
            r.Segment = s;
            r.Arc = a;
        }

        // Phase 4 相邻边夹角已禁用：当斜边由直角坐标(dx,dy)定义时，角度是冗余的(封闭尺寸链)，
        // 强行四舍五入还会造成数值矛盾(146.31°->146°，按 146° 加工落差变 20.235mm)。
        // GB/T 4458.4：倾斜边已由直角坐标定位时不得重复标角度。需要时由用户手动标参考尺寸(146.3°)。
        // r.Angular = AngularDimensioner.Annotate(...);

        return r;
    }
}
