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
    /// 接触图形。圆弧端头零件的角点(minX,bottomY)往往不存在，直接引线会悬空。</summary>
    public static (Point3d? Left, Point3d? Right, Point3d? Bottom, Point3d? Top) OutlineEdgePoints(
        Transaction tr, IReadOnlyList<ObjectId> ids, Extents3d ext)
    {
        double minX = ext.MinPoint.X, maxX = ext.MaxPoint.X;
        double minY = ext.MinPoint.Y, maxY = ext.MaxPoint.Y;
        Point3d? left = null, right = null, bottom = null, top = null;
        double dl = double.MaxValue, dr = double.MaxValue, db = double.MaxValue, dt = double.MaxValue;
        foreach (var id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Polyline pl) continue;
            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                var p = pl.GetPoint2dAt(i);
                var p3 = new Point3d(p.X, p.Y, 0);
                double ld = System.Math.Abs(p.X - minX);
                if (ld < dl) { dl = ld; left = p3; }
                double rd = System.Math.Abs(p.X - maxX);
                if (rd < dr) { dr = rd; right = p3; }
                double bd = System.Math.Abs(p.Y - minY);
                if (bd < db) { db = bd; bottom = p3; }
                double td = System.Math.Abs(p.Y - maxY);
                if (td < dt) { dt = td; top = p3; }
            }
        }
        return (left, right, bottom, top);
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
