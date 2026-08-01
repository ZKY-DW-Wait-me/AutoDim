using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoDim.Cad;

namespace AutoDim.Dimensioning;

/// <summary>把标注实体加入模型空间的公共工具。</summary>
internal static class DimUtil
{
    /// <summary>跟随标注样式的注释性(annotative)：模板样式若是注释性的，标注也必须注释性，
    /// 否则文字/箭头大小按固定比例显示，与手动标注不一致(尤其布局/视口)。</summary>
    public static void InheritAnnotative(Transaction tr, Dimension dim, ObjectId dimStyleId)
    {
        try
        {
            var dst = (DimStyleTableRecord)tr.GetObject(dimStyleId, OpenMode.ForRead);
            if (dst.Annotative == AnnotativeStates.True)
                dim.Annotative = AnnotativeStates.True;
        }
        catch { }
    }

    public static void Append(Database db, Transaction tr, BlockTableRecord space,
                              Dimension dim, ObjectId dimStyleId, ObjectId layerId,
                              string? overrideText = null)
    {
        // 顺序：SetDatabaseDefaults(按当前样式初始化) -> 强制 ADIM 样式 -> 入库 -> 最后再覆盖文字。
        // DimensionText 必须在 SetDatabaseDefaults/DimensionStyle 之后设，否则会被重置回自动测量。
        dim.SetDatabaseDefaults(db);
        dim.DimensionStyle = dimStyleId;
        dim.LayerId = layerId;
        InheritAnnotative(tr, dim, dimStyleId);
        space.AppendEntity(dim);
        tr.AddNewlyCreatedDBObject(dim, true);
        if (!string.IsNullOrEmpty(overrideText))
        {
            dim.DimensionText = overrideText;
            dim.RecomputeDimensionBlock(true);   // 强制重算标注块，否则显示缓存里仍是自动测量的 4 位小数
        }
        // 打 AUTODIM XData 标记：重跑时只删自己生成的，不误擦用户手标
        AdimMarker.Mark(db, tr, dim);
    }
}

/// <summary>
/// ① 总体外形尺寸：宽(水平) + 高(垂直)。
/// 放在最外层 tier（离零件最远），给内层的定位/分段尺寸让位，避免与之重叠（GB 分层惯例）。
/// </summary>
internal static class OverallDimensioner
{
    /// <summary>轮廓边界点：包围盒各边上"真实存在的轮廓顶点"——总体尺寸的界线必须
    /// 接触图形。
    /// 取点规则（GB 总体尺寸）：长/宽 = 两条平行边之间的距离，界线优先取
    /// "贴边的直线段中点"（圆角矩形标原始长宽，而不是两段四分之一圆弧末端捕捉点）；
    /// 该侧没有直线段（纯圆弧端头）时才取圆弧极值点，保证界线永不悬空。</summary>
    public static (Point3d? Left, Point3d? Right, Point3d? Bottom, Point3d? Top) OutlineEdgePoints(
        Transaction tr, IReadOnlyList<ObjectId> ids, Extents3d ext)
    {
        double minX = ext.MinPoint.X, maxX = ext.MaxPoint.X;
        double minY = ext.MinPoint.Y, maxY = ext.MaxPoint.Y;
        const double tol = 1e-6;
        Point3d? left = null, right = null, bottom = null, top = null;

        foreach (var id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent) continue;
            switch (ent)
            {
                case Polyline pl:
                {
                    int n = pl.NumberOfVertices;
                    for (int i = 0; i < n; i++)
                    {
                        if (!pl.Closed && i == n - 1) break;
                        int j = (i + 1) % n;
                        var a = pl.GetPoint2dAt(i);
                        var b = pl.GetPoint2dAt(j);
                        if (System.Math.Abs(pl.GetBulgeAt(i)) < 1e-9)
                            ConsiderEdgeLine(a, b, minX, maxX, minY, maxY, tol,
                                             ref left, ref right, ref bottom, ref top);
                        else
                        {
                            var arcSeg = pl.GetArcSegment2dAt(i);
                            ConsiderEdgeArc(arcSeg.Center, arcSeg.Radius, arcSeg.StartAngle, arcSeg.EndAngle,
                                            minX, maxX, minY, maxY, tol,
                                            ref left, ref right, ref bottom, ref top);
                        }
                    }
                    break;
                }
                case Line ln:
                    ConsiderEdgeLine(new Point2d(ln.StartPoint.X, ln.StartPoint.Y),
                                     new Point2d(ln.EndPoint.X, ln.EndPoint.Y),
                                     minX, maxX, minY, maxY, tol,
                                     ref left, ref right, ref bottom, ref top);
                    break;
                case Arc ar:
                {
                    double a0 = ar.StartAngle, a1 = ar.EndAngle;
                    if (a1 <= a0) a1 += 2.0 * System.Math.PI;
                    ConsiderEdgeArc(new Point2d(ar.Center.X, ar.Center.Y), ar.Radius, a0, a1,
                                    minX, maxX, minY, maxY, tol,
                                    ref left, ref right, ref bottom, ref top);
                    break;
                }
            }
        }
        return (left, right, bottom, top);
    }

    /// <summary>直线段贴包围盒某条边时，取该直线段中点作为总体尺寸界线落点
    /// （两条平行线之间距离的基准，避免落到圆弧末端捕捉点）。</summary>
    private static void ConsiderEdgeLine(Point2d a, Point2d b,
        double minX, double maxX, double minY, double maxY, double tol,
        ref Point3d? left, ref Point3d? right, ref Point3d? bottom, ref Point3d? top)
    {
        if (System.Math.Abs(a.Y - b.Y) < tol)
        {
            if (System.Math.Abs(a.Y - minY) < tol)
                bottom = new Point3d((a.X + b.X) * 0.5, minY, 0);
            if (System.Math.Abs(a.Y - maxY) < tol)
                top = new Point3d((a.X + b.X) * 0.5, maxY, 0);
        }
        else if (System.Math.Abs(a.X - b.X) < tol)
        {
            if (System.Math.Abs(a.X - minX) < tol)
                left = new Point3d(minX, (a.Y + b.Y) * 0.5, 0);
            if (System.Math.Abs(a.X - maxX) < tol)
                right = new Point3d(maxX, (a.Y + b.Y) * 0.5, 0);
        }
    }

    /// <summary>圆弧的极值点（最左/最右/最下/最上）若恰好贴包围盒边，取作界线落点——
    /// 该侧没有直线段（纯圆弧端头）时使用，保证界线仍与图形接触。</summary>
    private static void ConsiderEdgeArc(Point2d c, double r, double a0, double a1,
        double minX, double maxX, double minY, double maxY, double tol,
        ref Point3d? left, ref Point3d? right, ref Point3d? bottom, ref Point3d? top)
    {
        void At(double ang, bool asLeft, bool asRight, bool asBottom, bool asTop,
            ref Point3d? holder)
        {
            double t = ang % (2.0 * System.Math.PI);
            if (t < 0) t += 2.0 * System.Math.PI;
            if (t < a0 - 1e-9 || t > a1 + 1e-9) return;
            var p = new Point2d(c.X + r * System.Math.Cos(t), c.Y + r * System.Math.Sin(t));
            if (asLeft && System.Math.Abs(p.X - minX) < tol) holder = new Point3d(p.X, p.Y, 0);
            if (asRight && System.Math.Abs(p.X - maxX) < tol) holder = new Point3d(p.X, p.Y, 0);
            if (asBottom && System.Math.Abs(p.Y - minY) < tol) holder = new Point3d(p.X, p.Y, 0);
            if (asTop && System.Math.Abs(p.Y - maxY) < tol) holder = new Point3d(p.X, p.Y, 0);
        }
        At(System.Math.PI, true, false, false, false, ref left);           // 180°：x 最小
        At(0.0, false, true, false, false, ref right);                     // 0°：x 最大
        At(System.Math.PI * 1.5, false, false, true, false, ref bottom);   // 270°：y 最小
        At(System.Math.PI * 0.5, false, false, false, true, ref top);      // 90°：y 最大
    }

    public static int Annotate(Database db, Transaction tr, BlockTableRecord space,
                               Extents3d? ext, Point3d? left, Point3d? right,
                               Point3d? bottom, Point3d? top,
                               ObjectId dimStyleId, ObjectId layerId, double baseGap,
                               bool skipWidth = false, bool skipHeight = false)
    {
        if (ext == null) return 0;

        Extents3d e = ext.Value;
        Point3d min = e.MinPoint, max = e.MaxPoint;
        double w = max.X - min.X, h = max.Y - min.Y;

        double off = 3.5 * baseGap;   // 总体尺寸放最外层(远离零件)，给内层定位/分段留足空间，避免尺寸线穿过内层文字
        int count = 0;

        if (w > 1e-9 && !skipWidth)
        {
            // 宽度尺寸：界线从"最左/最右轮廓点"引到下方尺寸线(接触图形)，
            // 而不是不存在的包围盒角点(圆弧端头零件的角点悬空)
            var p1 = left ?? new Point3d(min.X, min.Y, 0);
            var p2 = right ?? new Point3d(max.X, min.Y, 0);
            var dl = new Point3d((min.X + max.X) * 0.5, min.Y - off, 0);
            DimUtil.Append(db, tr, space, new RotatedDimension(0.0, p1, p2, dl, "", dimStyleId), dimStyleId, layerId, FormatLen(w));
            count++;
        }
        if (h > 1e-9 && !skipHeight)
        {
            // 高度尺寸：界线从"最下/最上轮廓点"引到左侧尺寸线(接触图形)
            var p1 = bottom ?? new Point3d(min.X, min.Y, 0);
            var p2 = top ?? new Point3d(min.X, max.Y, 0);
            var dl = new Point3d(min.X - off, (min.Y + max.Y) * 0.5, 0);
            DimUtil.Append(db, tr, space, new RotatedDimension(System.Math.PI * 0.5, p1, p2, dl, "", dimStyleId), dimStyleId, layerId, FormatLen(h));
            count++;
        }
        return count;
    }

    /// <summary>长度格式化：整数不带小数，否则最多2位并去尾零。</summary>
    private static string FormatLen(double v)
    {
        double r = System.Math.Round(v, 2);
        if (System.Math.Abs(r - System.Math.Round(r)) < 1e-9)
            return ((long)System.Math.Round(r)).ToString();
        return r.ToString("0.##");
    }
}
