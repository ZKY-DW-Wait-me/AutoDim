using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoDim.Cad;

namespace AutoDim.Dimensioning;

/// <summary>把标注实体加入模型空间的公共工具。</summary>
internal static class DimUtil
{
    public static void Append(Database db, Transaction tr, BlockTableRecord space,
                              Dimension dim, ObjectId dimStyleId, ObjectId layerId,
                              string? overrideText = null)
    {
        // 顺序：SetDatabaseDefaults(按当前样式初始化) -> 强制 ADIM 样式 -> 入库 -> 最后再覆盖文字。
        // DimensionText 必须在 SetDatabaseDefaults/DimensionStyle 之后设，否则会被重置回自动测量。
        dim.SetDatabaseDefaults(db);
        dim.DimensionStyle = dimStyleId;
        dim.LayerId = layerId;
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
    public static int Annotate(Database db, Transaction tr, BlockTableRecord space,
                               Extents3d? ext, ObjectId dimStyleId, ObjectId layerId, double baseGap,
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
            var p1 = new Point3d(min.X, min.Y, 0);
            var p2 = new Point3d(max.X, min.Y, 0);
            var dl = new Point3d((min.X + max.X) * 0.5, min.Y - off, 0);
            DimUtil.Append(db, tr, space, new RotatedDimension(0.0, p1, p2, dl, "", dimStyleId), dimStyleId, layerId, FormatLen(w));
            count++;
        }
        if (h > 1e-9 && !skipHeight)
        {
            var p1 = new Point3d(min.X, min.Y, 0);
            var p2 = new Point3d(min.X, max.Y, 0);
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
