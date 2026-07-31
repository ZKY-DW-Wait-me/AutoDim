using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoDim;

/// <summary>
/// 生成一张复杂复合测试图，覆盖 Phase 1~5 的所有分支：
///   ① 主零件(左下)：L 形 + 多处圆角 + 顶部斜边 + 3 个不等径孔。
///      触发：总体外形、分段(水平/垂直/斜/圆弧)、孔直径+定位、中心线、圆角 R 中心线。
///   ② 远距离小零件(右上)：矩形 + 2 小孔，距主零件约 300mm——验证两图同标字号不被拉爆。
///   ③ 任意倾斜多边形(右下)：独立 Line 拼成的五边形，无水平/垂直边——触发 Mode B(边长+夹角)。
/// 画完命令行打印提示。
/// </summary>
internal static class SampleBuilder
{
    public static void Build(Document? doc)
    {
        if (doc == null) return;
        Database db = doc.Database;

        using Transaction tr = db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

        void Append(Entity e)
        {
            e.SetDatabaseDefaults(db);
            ms.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
        }

        // ① 主零件
        var main = new Polyline();
        // 逆时针；bulge<0 = 顺时针凸弧(圆角 R20)
        main.AddVertexAt(0, new Point2d(0,   0),  0,        0, 0);   // 左下
        main.AddVertexAt(1, new Point2d(120, 0),  0,        0, 0);   // 右下
        main.AddVertexAt(2, new Point2d(120, 40), -0.4142,  0, 0);   // 右下→右上 R20 圆角起点
        main.AddVertexAt(3, new Point2d(100, 60), 0,        0, 0);   // 圆角终点
        main.AddVertexAt(4, new Point2d(70,  60), -0.4142,  0, 0);   // 顶部右段→左上 R20 圆角起点
        main.AddVertexAt(5, new Point2d(50,  80), 0,        0, 0);   // 圆角终点
        main.AddVertexAt(6, new Point2d(20,  80), 0,        0, 0);   // 左上平台
        main.AddVertexAt(7, new Point2d(0,   60), 0,        0, 0);   // 左上斜边
        main.Closed = true;
        Append(main);
        Append(new Circle(new Point3d(30, 30, 0), Vector3d.ZAxis, 8.0));
        Append(new Circle(new Point3d(95, 20, 0), Vector3d.ZAxis, 12.0));
        Append(new Circle(new Point3d(35, 65, 0), Vector3d.ZAxis, 6.0));

        // ② 远距离小零件(右上)，距主零件 ~300mm
        double ox = 400, oy = 200;
        var far = new Polyline();
        far.AddVertexAt(0, new Point2d(ox,      oy),      0, 0, 0);
        far.AddVertexAt(1, new Point2d(ox + 60, oy),     0, 0, 0);
        far.AddVertexAt(2, new Point2d(ox + 60, oy + 40), 0, 0, 0);
        far.AddVertexAt(3, new Point2d(ox,      oy + 40), 0, 0, 0);
        far.Closed = true;
        Append(far);
        Append(new Circle(new Point3d(ox + 15, oy + 20, 0), Vector3d.ZAxis, 5.0));
        Append(new Circle(new Point3d(ox + 45, oy + 20, 0), Vector3d.ZAxis, 5.0));

        // ③ 任意倾斜多边形(右下)：独立 Line 拼成的五边形，全部倾斜边——触发 Mode B
        double[][] pts =
        {
            new[] { 250.0,  50.0 },
            new[] { 320.0,  35.0 },
            new[] { 340.0,  95.0 },
            new[] { 290.0, 130.0 },
            new[] { 235.0, 110.0 },
        };
        int n = pts.Length;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            Append(new Line(new Point3d(pts[i][0], pts[i][1], 0),
                            new Point3d(pts[j][0], pts[j][1], 0)));
        }

        tr.Commit();
        doc.Editor.WriteMessage(
            "\nADIMSAMPLE: 已生成复杂复合测试图(主零件 + 远距离小零件 + 任意倾斜多边形)。\n" +
            "建议依次测试：ADIMALL / ADIMSEL(框选主零件) / ADIMSEL(框选多边形) / ADIMCOORD(选多边形) / ADIMSCALE。\n");
    }
}
