using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoDim.Cad;

namespace AutoDim.Dimensioning;

/// <summary>
/// 模式 A：坐标标注法。以最左下顶点为基准，每个顶点引出 X 坐标和 Y 坐标，
/// 用 OrdinateDimension 形成坐标链。无角度弧、无方向歧义、转角不拥挤。
/// 支持 Polyline 和独立 Line 实体(端点汇总去重)。
/// </summary>
internal static class CoordinateDimensioner
{
    private const double Tol = 1e-6;

    /// <returns>标出的 X/Y 坐标数。</returns>
    public static int Annotate(Database db, Transaction tr, BlockTableRecord space,
        IReadOnlyList<ObjectId> ids, Extents3d? ext, ObjectId dimStyleId, ObjectId layerId, double baseGap)
    {
        var verts = CollectVertices(tr, ids);
        if (verts.Count < 2) return 0;

        // 基准点：X 最小且 Y 最小(最左下顶点)
        Point2d origin = verts[0];
        foreach (var v in verts)
        {
            if (v.X < origin.X - Tol || (System.Math.Abs(v.X - origin.X) < Tol && v.Y < origin.Y - Tol))
                origin = v;
        }

        // 坐标链偏移量：沿 X 轴标到包围盒下方、沿 Y 轴标到包围盒左侧
        double off = 1.5 * baseGap;
        double minX = ext?.MinPoint.X ?? verts.Min(v => v.X);
        double minY = ext?.MinPoint.Y ?? verts.Min(v => v.Y);

        int count = 0;
        var xDone = new HashSet<double>(new DoubleEq());
        var yDone = new HashSet<double>(new DoubleEq());

        foreach (var v in verts)
        {
            // X 坐标(相对基准的水平距离)
            double dx = v.X - origin.X;
            if (xDone.Add(v.X) && System.Math.Abs(dx) > Tol)
            {
                var dim = new OrdinateDimension(true,
                    new Point3d(v.X, v.Y, 0),                                  // 测量点
                    new Point3d(v.X, minY - off, 0),                            // 引出端(尺寸线在底边下方)
                    "", dimStyleId);
                dim.DimensionText = FormatLen(dx);
                DimUtil.Append(db, tr, space, dim, dimStyleId, layerId, FormatLen(dx));
                count++;
            }
            // Y 坐标(相对基准的垂直距离)
            double dy = v.Y - origin.Y;
            if (yDone.Add(v.Y) && System.Math.Abs(dy) > Tol)
            {
                var dim = new OrdinateDimension(false,
                    new Point3d(v.X, v.Y, 0),
                    new Point3d(minX - off, v.Y, 0),                            // 引出端(尺寸线在左边外侧)
                    "", dimStyleId);
                DimUtil.Append(db, tr, space, dim, dimStyleId, layerId, FormatLen(dy));
                count++;
            }
        }
        return count;
    }

    /// <summary>汇总所有顶点(Polyline 顶点 + Line 端点)，去重。</summary>
    private static List<Point2d> CollectVertices(Transaction tr, IReadOnlyList<ObjectId> ids)
    {
        var pts = new List<Point2d>();
        foreach (var id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is Polyline pl)
            {
                int n = pl.NumberOfVertices;
                for (int i = 0; i < n; i++)
                    pts.Add(pl.GetPoint2dAt(i));
            }
            else if (tr.GetObject(id, OpenMode.ForRead) is Line ln)
            {
                pts.Add(new Point2d(ln.StartPoint.X, ln.StartPoint.Y));
                pts.Add(new Point2d(ln.EndPoint.X, ln.EndPoint.Y));
            }
        }
        return pts;
    }

    private static string FormatLen(double v)
    {
        double r = System.Math.Round(v, 2);
        if (System.Math.Abs(r - System.Math.Round(r)) < 1e-9)
            return ((long)System.Math.Round(r)).ToString();
        return r.ToString("0.##");
    }

    private sealed class DoubleEq : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => System.Math.Abs(x - y) < Tol;
        public int GetHashCode(double obj) => System.Math.Round(obj, 3).GetHashCode();
    }
}
