using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoDim.Dimensioning;

/// <summary>给圆/圆弧补画十字中心线(GB/T 4458.1：细点画线，超出轮廓 2~5mm)。</summary>
internal static class CenterlineHelper
{
    private const string CenterLayer = "ADIM_CENTER";

    public static void AddCross(Database db, Transaction tr, BlockTableRecord space,
        Point3d center, double radius, Extents3d ext)
    {
        ObjectId layerId = EnsureLayer(db, tr);
        ObjectId centerLtId = EnsureCenterLinetype(db, tr);
        double overhang = System.Math.Max(radius * 0.3, 3.0);   // 超出圆周 3mm(GB 推荐 2~5mm)

        AddLine(db, tr, space, layerId, centerLtId,
            new Point3d(center.X - radius - overhang, center.Y, 0),
            new Point3d(center.X + radius + overhang, center.Y, 0));
        AddLine(db, tr, space, layerId, centerLtId,
            new Point3d(center.X, center.Y - radius - overhang, 0),
            new Point3d(center.X, center.Y + radius + overhang, 0));
    }

    private static void AddLine(Database db, Transaction tr, BlockTableRecord space,
        ObjectId layerId, ObjectId ltId, Point3d a, Point3d b)
    {
        var ln = new Line(a, b);
        ln.SetDatabaseDefaults(db);
        ln.LayerId = layerId;
        ln.LinetypeId = ltId;        // 显式指定 CENTER 线型记录
        space.AppendEntity(ln);
        tr.AddNewlyCreatedDBObject(ln, true);
        Cad.AdimMarker.Mark(db, tr, ln);   // 打标记，重跑只删自己生成的中心线
    }

    private static ObjectId EnsureLayer(Database db, Transaction tr)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(CenterLayer)) return lt[CenterLayer];
        lt.UpgradeOpen();
        var rec = new LayerTableRecord { Name = CenterLayer, Color = Color.FromColorIndex(ColorMethod.ByAci, 1) };
        ObjectId id = lt.Add(rec);
        tr.AddNewlyCreatedDBObject(rec, true);
        return id;
    }

    /// <summary>确保 CENTER 线型已加载到当前图纸，返回其 ObjectId。</summary>
    private static ObjectId EnsureCenterLinetype(Database db, Transaction tr)
    {
        var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        if (ltt.Has("CENTER")) return ltt["CENTER"];
        if (ltt.Has("CENTER2")) return ltt["CENTER2"];
        // 从 acadiso.lin 加载 CENTER(文件名只给名字，AutoCAD 在支持文件路径里自动找)
        ltt.UpgradeOpen();
        try { db.LoadLineTypeFile("CENTER", "acadiso.lin"); }
        catch { try { db.LoadLineTypeFile("CENTER", "acad.lin"); } catch { } }
        if (ltt.Has("CENTER")) return ltt["CENTER"];
        return ltt["Continuous"];
    }
}
