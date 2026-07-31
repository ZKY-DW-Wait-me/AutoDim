using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoDim.Cad;

namespace AutoDim.Dimensioning;

/// <summary>
/// 任意倾斜多边形标注(模式 B：对齐边长 + 关键夹角)。
/// 支持 Polyline 和独立 Line 实体组成的边集。
/// 当边集里【没有水平/垂直直边】时启用——外包框总体尺寸挂空无意义，直角坐标无基准。
/// GB 做法：每条边标实长(AlignedDimension)，相邻边(端点共享)标夹角(LineAngularDimension2，
/// 1 位小数避免整数度四舍五入致加工矛盾，文字水平书写)。
/// </summary>
internal static class ArbitraryPolygonDimensioner
{
    private const double Tol = 1e-6;

    public static (int edges, int angles) Annotate(
        Database db, Transaction tr, BlockTableRecord space,
        IReadOnlyList<ObjectId> ids, ObjectId dimStyleId, ObjectId layerId, double baseGap)
    {
        var edges = CollectEdges(tr, ids);
        if (edges.Count < 3) return (0, 0);
        if (!IsArbitrary(edges)) return (0, 0);

        Point2d centroid = ComputeCentroid(edges);
        int eCount = 0, aCount = 0;

        foreach (var e in edges)
        {
            AddAlignedEdge(db, tr, space, e.A, e.B, centroid, dimStyleId, layerId, baseGap);
            eCount++;
        }

        // 相邻边(端点共享)标夹角
        for (int i = 0; i < edges.Count; i++)
        {
            for (int j = i + 1; j < edges.Count; j++)
            {
                Point2d? shared = SharedEndpoint(edges[i], edges[j]);
                if (shared == null) continue;
                Point2d other_i = (edges[i].A == shared.Value) ? edges[i].B : edges[i].A;
                Point2d other_j = (edges[j].A == shared.Value) ? edges[j].B : edges[j].A;
                if (AddVertexAngle(db, tr, space, other_i, shared.Value, other_j, centroid, dimStyleId, layerId, baseGap))
                    aCount++;
            }
        }
        return (eCount, aCount);
    }

    private record struct Edge(Point2d A, Point2d B);

    /// <summary>从 Polyline 顶点 + 独立 Line 实体收集所有直边。</summary>
    private static List<Edge> CollectEdges(Transaction tr, IReadOnlyList<ObjectId> ids)
    {
        var list = new List<Edge>();
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
                    list.Add(new Edge(pl.GetPoint2dAt(i), pl.GetPoint2dAt(j)));
                }
            }
            else if (tr.GetObject(id, OpenMode.ForRead) is Line ln)
            {
                list.Add(new Edge(new Point2d(ln.StartPoint.X, ln.StartPoint.Y),
                                  new Point2d(ln.EndPoint.X, ln.EndPoint.Y)));
            }
        }
        return list;
    }

    /// <summary>所有边都不水平、不垂直才算"任意多边形"。</summary>
    private static bool IsArbitrary(List<Edge> edges)
    {
        foreach (var e in edges)
        {
            if (System.Math.Abs(e.A.X - e.B.X) < Tol) return false;
            if (System.Math.Abs(e.A.Y - e.B.Y) < Tol) return false;
        }
        return true;
    }

    /// <summary>两条边是否共享端点(坐标相等在 tol 内)。返回共享点，否则 null。</summary>
    private static Point2d? SharedEndpoint(Edge e1, Edge e2)
    {
        if (Same(e1.A, e2.A) || Same(e1.A, e2.B)) return e1.A;
        if (Same(e1.B, e2.A) || Same(e1.B, e2.B)) return e1.B;
        return null;
    }
    private static bool Same(Point2d a, Point2d b) =>
        System.Math.Abs(a.X - b.X) < Tol && System.Math.Abs(a.Y - b.Y) < Tol;

    private static void AddAlignedEdge(Database db, Transaction tr, BlockTableRecord space,
        Point2d a, Point2d b, Point2d centroid, ObjectId dimStyleId, ObjectId layerId, double baseGap)
    {
        Vector2d d = b - a;
        double len = d.Length;
        if (len < Tol) return;
        Vector2d n2 = new Vector2d(-d.Y, d.X) / len;
        Point2d mid = a + d * 0.5;
        if (n2.DotProduct(centroid - mid) > 0) n2 = -n2;
        // 边长尺寸线偏移：跟边长挂钩(0.15×len)，clamp 到 [4, 10]mm，避免大图偏太远跟角度弧打架。
        double off = System.Math.Clamp(0.15 * len, 4.0, 10.0);
        Point2d dl2 = mid + n2 * off;
        var dim = new AlignedDimension(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0),
                                       new Point3d(dl2.X, dl2.Y, 0), "", dimStyleId);
        DimUtil.Append(db, tr, space, dim, dimStyleId, layerId, FormatLen(len));
    }

    /// <summary>p1 为顶点，p0-p1 与 p1-p2 的夹角。
    /// 跳过接近 0°(退化)、180°(共线)、90°(直角，任意多边形里直角由相邻边长隐含确定，标了冗余)。
    /// 内角侧判定：用 d1×d2 叉积符号决定 arcPt 走内角一侧(锐角优先)，避免标在外侧。
    /// 弧半径 = 0.25×min(相邻两边长)，且 clamp 到 [5, 15]mm，避免"雷达盘"式巨大弧。</summary>
    private static bool AddVertexAngle(Database db, Transaction tr, BlockTableRecord space,
        Point2d p0, Point2d p1, Point2d p2, Point2d centroid,
        ObjectId dimStyleId, ObjectId layerId, double baseGap)
    {
        Vector2d d1 = p0 - p1, d2 = p2 - p1;
        double len1 = d1.Length, len2 = d2.Length;
        if (len1 < Tol || len2 < Tol) return false;
        double cos = d1.DotProduct(d2) / (len1 * len2);
        double ang = System.Math.Acos(System.Math.Clamp(cos, -1.0, 1.0));
        const double FiveDeg = 5.0 * System.Math.PI / 180.0;
        if (ang < FiveDeg) return false;                              // 退化
        if (System.Math.Abs(ang - System.Math.PI) < FiveDeg) return false;   // 共线
        if (System.Math.Abs(ang - System.Math.PI * 0.5) < FiveDeg) return false; // 直角跳过

        // 角平分线方向(从 p1 指向两射线中点)。内/外角判定：叉积 d1×d2 的符号决定弧放哪侧。
        Vector2d u1 = d1 / len1, u2 = d2 / len2;
        Vector2d bis = u1 + u2;
        if (bis.Length < Tol) return false;
        bis = bis / bis.Length;
        // 叉积 z = u1.x*u2.y - u1.y*u2.x。>0 表示 p2 在 p0->p1 的左侧(逆时针)，弧放该侧标内角。
        double cross = u1.X * u2.Y - u1.Y * u2.X;
        if (cross < 0) bis = -bis;

        // 弧半径：跟局部最短边挂钩(0.3×min)，clamp 到 [4, 12]mm。短边时弧更小，避免跟边长尺寸线挤在转角。
        double r = System.Math.Clamp(0.3 * System.Math.Min(len1, len2), 4.0, 12.0);
        // 仅当相邻边极短(<15mm，几何上没意义的角)才跳过；普通角即使边稍短也标，避免漏标。
        if (System.Math.Min(len1, len2) < 15.0) return false;
        var arcPt = new Point3d(p1.X + bis.X * r, p1.Y + bis.Y * r, 0);

        var dim = new LineAngularDimension2(new Point3d(p1.X, p1.Y, 0),
                                            new Point3d(p0.X, p0.Y, 0),
                                            new Point3d(p1.X, p1.Y, 0),
                                            new Point3d(p2.X, p2.Y, 0),
                                            arcPt, "", dimStyleId);
        dim.SetDatabaseDefaults(db);
        dim.DimensionStyle = dimStyleId;
        dim.LayerId = layerId;
        space.AppendEntity(dim);
        tr.AddNewlyCreatedDBObject(dim, true);
        dim.TextRotation = 0.0;
        double deg = ang * 180.0 / System.Math.PI;
        dim.DimensionText = deg.ToString("F1") + "°";
        dim.RecomputeDimensionBlock(true);
        Cad.AdimMarker.Mark(db, tr, dim);
        return true;
    }

    /// <summary>边集质心：所有边中点的平均(无面积可用，退化安全)。</summary>
    private static Point2d ComputeCentroid(List<Edge> edges)
    {
        double cx = 0, cy = 0;
        foreach (var e in edges) { cx += (e.A.X + e.B.X) * 0.5; cy += (e.A.Y + e.B.Y) * 0.5; }
        return new Point2d(cx / edges.Count, cy / edges.Count);
    }

    private static string FormatLen(double v)
    {
        double r = System.Math.Round(v, 2);
        if (System.Math.Abs(r - System.Math.Round(r)) < 1e-9)
            return ((long)System.Math.Round(r)).ToString();
        return r.ToString("0.##");
    }
}
