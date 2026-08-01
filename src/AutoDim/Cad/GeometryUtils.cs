using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoDim.Cad;

internal static class GeometryUtils
{
    /// <summary>合并给定实体的几何包围盒；无有效实体返回 null。</summary>
    public static Extents3d? CombinedExtents(Transaction tr, IReadOnlyList<ObjectId> ids)
    {
        Extents3d? acc = null;
        foreach (var id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent) continue;

            Extents3d ge;
            try { ge = ent.GeometricExtents; }
            catch { continue; } // 退化/空实体访问 GeometricExtents 可能抛异常，跳过

            if (acc == null) acc = ge;
            else { var a = acc.Value; a.AddExtents(ge); acc = a; }
        }
        return acc;
    }

    /// <summary>依据包围盒较大边推算一个默认偏移量（不小于极小值）。</summary>
    public static double AutoGap(Extents3d e, double factor = 0.12)
    {
        double w = e.MaxPoint.X - e.MinPoint.X;
        double h = e.MaxPoint.Y - e.MinPoint.Y;
        return System.Math.Max(System.Math.Max(w, h) * factor, 1e-6);
    }

    /// <summary>
    /// 判断轮廓是否已沿包围盒某侧把整宽/整高"分段标满"。判定方式：收集贴左边或贴右边的所有垂直段
    /// 的 [yMin, yMax] 区间，若这些区间不重叠地合并后覆盖 [minY, maxY]，说明高度已被分段标全 ->
    /// 总体高跳过(避免封闭链)。宽度同理(贴底/贴顶的水平段覆盖 [minX, maxX])。
    /// 例：左边 20(y0-20) + 10(y20-30)? ...只要拼起来覆盖 0..80 就跳过总体高 80。
    /// </summary>
    public static (bool skipW, bool skipH) DetectOutermostEdges(Transaction tr, IReadOnlyList<ObjectId> ids, Extents3d ext)
    {
        double minX = ext.MinPoint.X, maxX = ext.MaxPoint.X;
        double minY = ext.MinPoint.Y, maxY = ext.MaxPoint.Y;
        double tol = 1e-6;
        double totalW = maxX - minX, totalH = maxY - minY;

        var leftIntervals = new List<(double lo, double hi)>();
        var rightIntervals = new List<(double lo, double hi)>();
        var bottomIntervals = new List<(double lo, double hi)>();
        var topIntervals = new List<(double lo, double hi)>();

        foreach (var id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Polyline pl) continue;
            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                if (!pl.Closed && i == n - 1) break;
                int j = (i + 1) % n;
                if (System.Math.Abs(pl.GetBulgeAt(i)) > 1e-9) continue;
                Point2d a = pl.GetPoint2dAt(i), b = pl.GetPoint2dAt(j);
                if (System.Math.Abs(a.Y - b.Y) < tol)   // 水平段
                {
                    var iv = (System.Math.Min(a.X, b.X), System.Math.Max(a.X, b.X));
                    if (System.Math.Abs(a.Y - minY) < tol) bottomIntervals.Add(iv);
                    if (System.Math.Abs(a.Y - maxY) < tol) topIntervals.Add(iv);
                }
                else if (System.Math.Abs(a.X - b.X) < tol)  // 垂直段
                {
                    var iv = (System.Math.Min(a.Y, b.Y), System.Math.Max(a.Y, b.Y));
                    if (System.Math.Abs(a.X - minX) < tol) leftIntervals.Add(iv);
                    if (System.Math.Abs(a.X - maxX) < tol) rightIntervals.Add(iv);
                }
            }
        }

        // 只有当该方向最外边是"≥2 段拼接覆盖"时才跳过总体：分段会标出各段、尺寸链隐含总宽。
        // 单条整边(如简单矩形底边)会被 OutlineDimensioner 当作最外边跳过分段、总体必须标，
        // 否则两个逻辑互踢 -> 矩形 0 尺寸(总体 skip + 分段 skip 全落空)。
        bool skipW = (bottomIntervals.Count >= 2 && Covers(bottomIntervals, minX, maxX, tol)) ||
                     (topIntervals.Count >= 2 && Covers(topIntervals, minX, maxX, tol));
        bool skipH = (leftIntervals.Count >= 2 && Covers(leftIntervals, minY, maxY, tol)) ||
                     (rightIntervals.Count >= 2 && Covers(rightIntervals, minY, maxY, tol));
        return (skipW, skipH);
    }

    /// <summary>区间合并后是否覆盖 [lo, hi]（允许 tol 间隙）。</summary>
    private static bool Covers(List<(double lo, double hi)> intervals, double lo, double hi, double tol)
    {
        if (intervals.Count == 0) return false;
        var sorted = intervals.OrderBy(v => v.lo).ToList();
        double cur = lo;
        foreach (var iv in sorted)
        {
            if (iv.lo > cur + tol) return false;   // 有空隙
            if (iv.hi > cur) cur = iv.hi;
        }
        return cur >= hi - tol;
    }
}
