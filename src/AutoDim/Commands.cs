using Autodesk.AutoCAD.ApplicationServices;         // Document
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AutoDim.Cad;
using AutoDim.Config;
using AutoDim.Core;
using AutoDim.Dimensioning;
using AutoDim.Selection;
// Application 用别名精确指向 core 版本（accoremgd），兼容 accoreconsole 与完整 AutoCAD，
// 避免与 acmgd 的 ApplicationServices.Application 产生二义性。
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CPoint = AutoDim.Core.Geometry.Point2D;

[assembly: ExtensionApplication(typeof(AutoDim.PluginInit))]
[assembly: CommandClass(typeof(AutoDim.Commands))]

namespace AutoDim;

/// <summary>加载/卸载钩子：NETLOAD 时打印一行横幅，便于确认已加载（含无界面测试）。</summary>
public sealed class PluginInit : IExtensionApplication
{
    public void Initialize()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        doc?.Editor.WriteMessage(
            "\nAutoDim 已加载。命令: AUTODIM / ADIMALL / ADIMWIN / ADIMSEL / ADIMSAMPLE / " +
            "ADIMCOORD / ADIMSCALE / ADIMCFG / ADIMDEBUG / ADIMCLEAN\n");
    }

    public void Terminate() { }
}

public sealed class Commands
{
    /// <summary>主命令：pickfirst 优先，否则提示选择方式（选择对象/整图/窗口）。</summary>
    [CommandMethod("AUTODIM", CommandFlags.UsePickSet | CommandFlags.Modal)]
    public void AutoDim()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        Editor ed = doc.Editor;

        // 1) 先看命令前是否已选中对象
        ObjectId[]? ids = SelectionService.Acquire(ed, TriggerMode.Pickfirst);

        // 2) 没有则询问选择方式
        if (ids == null)
        {
            var kw = new PromptKeywordOptions("\n选择方式 [选择对象(S)/整图(A)/窗口(W)]: ");
            kw.Keywords.Add("S");
            kw.Keywords.Add("A");
            kw.Keywords.Add("W");
            kw.Keywords.Default = "S";
            kw.AllowNone = true;

            PromptResult kr = ed.GetKeywords(kw);
            if (kr.Status != PromptStatus.OK) return;

            TriggerMode mode = kr.StringResult switch
            {
                "A" => TriggerMode.All,
                "W" => TriggerMode.Window,
                _   => TriggerMode.Selection
            };
            ids = SelectionService.Acquire(ed, mode);
        }

        if (ids == null || ids.Length == 0)
        {
            ed.WriteMessage("\n未选择到有效对象（需要 直线/圆弧/圆/多段线）。\n");
            return;
        }

        RunEngine(doc, ids, new AutoDimOptions());
    }

    /// <summary>整图自动标注。</summary>
    [CommandMethod("ADIMALL", CommandFlags.Modal)]
    public void AdimAll() => RunWithMode(TriggerMode.All);

    /// <summary>框选区域内标注。</summary>
    [CommandMethod("ADIMWIN", CommandFlags.Modal)]
    public void AdimWin() => RunWithMode(TriggerMode.Window);

    /// <summary>对选中对象标注（pickfirst 优先，否则交互选择）。</summary>
    [CommandMethod("ADIMSEL", CommandFlags.UsePickSet | CommandFlags.Modal)]
    public void AdimSel()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        Editor ed = doc.Editor;

        ObjectId[]? ids = SelectionService.Acquire(ed, TriggerMode.Pickfirst)
                          ?? SelectionService.Acquire(ed, TriggerMode.Selection);
        if (ids == null) { ed.WriteMessage("\n未选择对象。\n"); return; }

        RunEngine(doc, ids, new AutoDimOptions());
    }

    /// <summary>生成测试图（100×60 矩形 + 2 孔）。</summary>
    [CommandMethod("ADIMSAMPLE", CommandFlags.Modal)]
    public void AdimSample() => SampleBuilder.Build(AcApp.DocumentManager.MdiActiveDocument);

    /// <summary>模式 A：坐标标注法。基准=最左下顶点，每个顶点引 X/Y 坐标到图边。
    /// 适用于任意倾斜多边形——无角度弧、无方向歧义、转角不拥挤(GB 复杂板类零件推荐画法)。</summary>
    [CommandMethod("ADIMCOORD", CommandFlags.UsePickSet | CommandFlags.Modal)]
    public void AdimCoord()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        Editor ed = doc.Editor;

        ObjectId[]? ids = SelectionService.Acquire(ed, TriggerMode.Pickfirst)
                          ?? SelectionService.Acquire(ed, TriggerMode.Selection);
        if (ids == null || ids.Length == 0) { ed.WriteMessage("\n未选择对象。\n"); return; }

        RunCoordEngine(doc, ids);
    }

    /// <summary>设置/清除 ADIM 标注的固定 Dimscale。
    /// 输入正数=固定 scale(持久化到 NOD，之后所有 ADIM 标注用此值，不再自适应)；
    /// 输入 0 或负数=清除固定值，恢复按包围盒自适应。</summary>
    [CommandMethod("ADIMSCALE", CommandFlags.Modal)]
    public void AdimScale()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        Database db = doc.Database;
        Editor ed = doc.Editor;

        using Transaction tr = db.TransactionManager.StartTransaction();
        double? cur = Cad.DimStyleSetup.ReadUserScale(db, tr);
        tr.Commit();
        ed.WriteMessage(cur.HasValue
            ? $"\n当前固定 Dimscale = {cur.Value} (输入 0 或负数恢复自适应)"
            : "\n当前为自适应 Dimscale (按包围盒，0.5~3.0)");

        var po = new PromptDoubleOptions("\n输入 Dimscale (0 或负数=恢复自适应): ");
        po.AllowNegative = true; po.AllowZero = true;
        var pr = ed.GetDouble(po);
        if (pr.Status != PromptStatus.OK) return;

        if (pr.Value <= 0)
        {
            Cad.DimStyleSetup.SetUserScale(db, null);
            ed.WriteMessage("\n已清除固定 Dimscale，恢复自适应。\n");
        }
        else
        {
            Cad.DimStyleSetup.SetUserScale(db, pr.Value);
            ed.WriteMessage($"\n已固定 Dimscale = {pr.Value}。后续 ADIM*/ADIMCOORD 标注将用此值。\n");
        }
    }

    /// <summary>
    /// 图纸清洗命令：对选中/整图执行 去重 -> 微段共线合并 -> 端点吸附 -> 闭合轮廓提取，
    /// 把清洗出的闭合轮廓与去重圆绘制到 ADIM_CLEAN 图层，供后续标注使用。
    /// </summary>
    [CommandMethod("ADIMCLEAN", CommandFlags.UsePickSet | CommandFlags.Modal)]
    public void AdimClean()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        ObjectId[]? ids = SelectionService.Acquire(ed, TriggerMode.Pickfirst)
                          ?? SelectionService.Acquire(ed, TriggerMode.All);
        if (ids == null || ids.Length == 0)
        {
            ed.WriteMessage("\n未选择到有效对象。\n");
            return;
        }

        var segs = new List<(CPoint A, CPoint B)>();
        var circles = new List<(CPoint Center, double Radius)>();
        try
        {
            using Transaction tr = db.TransactionManager.StartTransaction();
            foreach (var id in ids)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent) continue;
                switch (ent)
                {
                    case Line ln:
                        segs.Add((new CPoint(ln.StartPoint.X, ln.StartPoint.Y),
                                  new CPoint(ln.EndPoint.X, ln.EndPoint.Y)));
                        break;
                    case Polyline pl:
                    {
                        int n = pl.NumberOfVertices;
                        for (int i = 0; i < n - 1; i++)
                        {
                            var p1 = pl.GetPoint2dAt(i);
                            var p2 = pl.GetPoint2dAt(i + 1);
                            segs.Add((new CPoint(p1.X, p1.Y), new CPoint(p2.X, p2.Y)));
                        }
                        if (pl.Closed && n > 2)
                        {
                            var pn = pl.GetPoint2dAt(n - 1);
                            var p0 = pl.GetPoint2dAt(0);
                            segs.Add((new CPoint(pn.X, pn.Y), new CPoint(p0.X, p0.Y)));
                        }
                        break;
                    }
                    case Circle ci:
                        circles.Add((new CPoint(ci.Center.X, ci.Center.Y), ci.Radius));
                        break;
                    case Arc ar:
                    {
                        double a0 = ar.StartAngle, a1 = ar.EndAngle;
                        if (a1 <= a0) a1 += 2.0 * Math.PI;
                        int steps = Math.Max(2, (int)((a1 - a0) * ar.Radius / 2.0) + 1);
                        var c = ar.Center;
                        for (int i = 0; i < steps; i++)
                        {
                            double t0 = a0 + (a1 - a0) * i / steps;
                            double t1 = a0 + (a1 - a0) * (i + 1) / steps;
                            segs.Add((new CPoint(c.X + ar.Radius * Math.Cos(t0), c.Y + ar.Radius * Math.Sin(t0)),
                                      new CPoint(c.X + ar.Radius * Math.Cos(t1), c.Y + ar.Radius * Math.Sin(t1))));
                        }
                        break;
                    }
                }
            }

            var res = ContourExtractor.Process(segs, circles, new CleanOptions());
            LayerHelper.EnsureLayer(db, tr, "ADIM_CLEAN");
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var drawnFaceIdx = new List<int>();
            var drawnIds = new List<ObjectId>();
            int drawn = 0;
            for (int fi = 0; fi < res.Faces.Count; fi++)
            {
                var face = res.Faces[fi];
                if (face.Length < 3) continue;
                using var pl = new Polyline { Layer = "ADIM_CLEAN", Closed = true };
                for (int i = 0; i < face.Length; i++)
                    pl.AddVertexAt(i, new Point2d(face[i].X, face[i].Y), 0, 0, 0);
                var pid = ms.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);
                drawnFaceIdx.Add(fi);
                drawnIds.Add(pid);
                drawn++;
            }
            foreach (var (c, r) in res.UniqueCircles)
            {
                using var ci = new Circle(new Point3d(c.X, c.Y, 0), Vector3d.ZAxis, r)
                {
                    Layer = "ADIM_CLEAN"
                };
                var cid = ms.AppendEntity(ci);
                tr.AddNewlyCreatedDBObject(ci, true);
                drawnIds.Add(cid);
                drawn++;
            }
            tr.Commit();

            // 特征分组公差：按清洗结果包围盒对角线比例（至少 2mm）
            double x0 = double.MaxValue, y0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue;
            foreach (var s in res.CleanedSegments)
            {
                x0 = Math.Min(x0, Math.Min(s.A.X, s.B.X));
                y0 = Math.Min(y0, Math.Min(s.A.Y, s.B.Y));
                x1 = Math.Max(x1, Math.Max(s.A.X, s.B.X));
                y1 = Math.Max(y1, Math.Max(s.A.Y, s.B.Y));
            }
            double diag = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            double gapTol = Math.Max(1.0, diag * 0.001);

            ed.WriteMessage(
                $"\nADIMCLEAN: raw_segments={segs.Count} cleaned={res.CleanedSegments.Count} " +
                $"faces={res.Faces.Count} circles={res.UniqueCircles.Count} drawn={drawn} " +
                $"(layer=ADIM_CLEAN)\n");

            if (drawnIds.Count > 0)
            {
                var groups = FeatureGrouping.GroupFeatures(res.Faces, res.UniqueCircles, gapTol);
                ed.WriteMessage($"    -> 按 {groups.Count} 个特征组分别标注...\n");
                foreach (var g in groups)
                {
                    var gIds = new List<ObjectId>();
                    foreach (var fi in g.FaceIndices)
                    {
                        int pos = drawnFaceIdx.IndexOf(fi);
                        if (pos >= 0) gIds.Add(drawnIds[pos]);
                    }
                    foreach (var ci in g.CircleIndices)
                        gIds.Add(drawnIds[drawnFaceIdx.Count + ci]);
                    if (gIds.Count == 0) continue;
                    RunEngine(doc, gIds.ToArray(), new AutoDimOptions());
                }
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            ed.WriteMessage($"\nADIMCLEAN 出错: {ex.Message}\n");
        }
    }

    private static void RunCoordEngine(Document doc, ObjectId[] ids)
    {
        Database db = doc.Database;
        Editor ed = doc.Editor;
        int count = 0;
        try
        {
            using Transaction tr = db.TransactionManager.StartTransaction();
            Extents3d? extBox = Cad.GeometryUtils.CombinedExtents(tr, ids);
            ObjectId layerId = LayerHelper.EnsureLayer(db, tr, "ADIM");
            ObjectId dimStyleId = Cad.DimStyleSetup.EnsureStyle(db, tr, extBox);
            double baseGap = extBox != null ? GeometryUtils.AutoGap(extBox.Value) : 10.0;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            PurgeAdimEntities(tr, ms, "ADIM", extBox, 0);

            count = CoordinateDimensioner.Annotate(db, tr, ms, ids, extBox, dimStyleId, layerId, baseGap);
            tr.Commit();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            ed.WriteMessage($"\nADIMCOORD 出错: {ex.Message}\n"); return;
        }
        ed.WriteMessage($"\nADIMCOORD: coordinates={count} (基准=最左下顶点)\n");
    }

    /// <summary>
    /// 配置命令：查看/切换自动标注的类别开关，并打印标注样式系统变量与图中 Dimension 实体诊断信息。
    /// 持久化到 NOD(OptionsStore)，之后所有 ADIM* 命令按此开关运行。
    /// </summary>
    [CommandMethod("ADIMCFG", CommandFlags.Modal)]
    public void AdimCfg()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        Database db = doc.Database;
        Editor ed = doc.Editor;

        // 先读当前类别开关
        int cur;
        using (Transaction tr0 = db.TransactionManager.StartTransaction())
        {
            cur = Cad.OptionsStore.ReadCategories(db, tr0);
            tr0.Commit();
        }
        var curCat = (DimCategory)cur;
        ed.WriteMessage($"\n当前类别开关: Overall={curCat.HasFlag(DimCategory.Overall)} " +
                        $"Segment={curCat.HasFlag(DimCategory.Segment)} " +
                        $"Holes={curCat.HasFlag(DimCategory.Holes)} " +
                        $"Angular={curCat.HasFlag(DimCategory.Angular)}");

        var kw = new PromptKeywordOptions("\n切换类别 [总体(O)/分段(E)/孔圆(H)/全部(ALL)/清空(NONE)]: ");
        kw.Keywords.Add("O"); kw.Keywords.Add("E"); kw.Keywords.Add("H"); kw.Keywords.Add("ALL"); kw.Keywords.Add("NONE");
        kw.AllowNone = true;
        var kr = ed.GetKeywords(kw);
        if (kr.Status == PromptStatus.OK)
        {
            int next = kr.StringResult switch
            {
                "O"    => cur ^ (int)DimCategory.Overall,
                "E"    => cur ^ (int)DimCategory.Segment,
                "H"    => cur ^ (int)DimCategory.Holes,
                "ALL"  => (int)DimCategory.All,
                "NONE" => 0,
                _      => cur
            };
            Cad.OptionsStore.WriteCategories(db, next);
            var n = (DimCategory)next;
            ed.WriteMessage($"\n已设: Overall={n.HasFlag(DimCategory.Overall)} " +
                            $"Segment={n.HasFlag(DimCategory.Segment)} " +
                            $"Holes={n.HasFlag(DimCategory.Holes)} " +
                            $"Angular={n.HasFlag(DimCategory.Angular)}\n");
        }

        // 诊断输出：标注样式系统变量 + 图中 Dimension 实体
        ed.WriteMessage("\n== 标注样式系统变量 ==");
        string[] vars = { "DIMTAD", "DIMTIX", "DIMTIH", "DIMTOH", "DIMTXT", "DIMASZ", "DIMSCALE", "DIMJUST", "DIMTMOVE" };
        foreach (var v in vars)
        {
            try { ed.WriteMessage($"\n  {v} = {AcApp.GetSystemVariable(v)}"); }
            catch { ed.WriteMessage($"\n  {v} = (N/A)"); }
        }
        ed.WriteMessage($"\n  CurrentDimStyle = {db.Dimstyle}");

        ed.WriteMessage("\n== 图中 Dimension 实体 ==");
        using Transaction tr = db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        // 打印 ADIM 样式是否存在、其记录里的实际值
        try
        {
            using Transaction tr2 = db.TransactionManager.StartTransaction();
            var dst = (DimStyleTable)tr2.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            if (dst.Has("ADIM"))
            {
                var rec = (DimStyleTableRecord)tr2.GetObject(dst["ADIM"], OpenMode.ForRead);
                ed.WriteMessage($"\n  ADIM样式存在: Dimtxt={rec.Dimtxt} Dimasz={rec.Dimasz} Dimscale={rec.Dimscale} Dimtad={rec.Dimtad} Dimtix={rec.Dimtix}");
            }
            else ed.WriteMessage("\n  ADIM样式不存在!");
            tr2.Commit();
        }
        catch (System.Exception ex) { ed.WriteMessage($"\n  读ADIM样式失败: {ex.Message}"); }

        int k = 0;
        foreach (ObjectId id in ms)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Dimension d) continue;
            string sty = d.DimensionStyle.ToString();
            string geo = DescribeDim(d);
            ed.WriteMessage($"\n  [{k}] {d.GetType().Name} text=\"{d.DimensionText}\" style={sty} {geo} txtRot={d.TextRotation:F2} layer={d.Layer}");
            k++;
        }
        tr.Commit();
        ed.WriteMessage("\n");
    }

    /// <summary>
    /// 诊断命令：打印图中所有多段线的每段几何（直线/圆弧、端点、弧心、半径、bulge、弧中点、外凸方向），
    /// 用于核对圆角引线朝向等几何判断是否正确。不做任何标注。
    /// </summary>
    [CommandMethod("ADIMDEBUG", CommandFlags.Modal)]
    public void AdimDebug()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        Database db = doc.Database;
        Editor ed = doc.Editor;

        using Transaction tr = db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

        int idx = 0;
        foreach (ObjectId id in ms)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Polyline pl) continue;
            ed.WriteMessage($"\n[Polyline #{idx}] vertices={pl.NumberOfVertices} closed={pl.Closed}");
            Point2d c = DebugCentroid(pl);
            ed.WriteMessage($"  centroid=({c.X:F2},{c.Y:F2})");
            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                if (!pl.Closed && i == n - 1) break;
                int j = (i + 1) % n;
                double bulge = pl.GetBulgeAt(i);
                Point2d a = pl.GetPoint2dAt(i), b = pl.GetPoint2dAt(j);
                if (System.Math.Abs(bulge) < 1e-9)
                {
                    ed.WriteMessage($"\n  seg{i}: LINE ({a.X:F1},{a.Y:F1})->({b.X:F1},{b.Y:F1}) len={(b - a).Length:F2}");
                }
                else
                {
                    var arc = pl.GetArcSegment2dAt(i);
                    Point2d ce = arc.Center;
                    double r = arc.Radius;
                    Vector2d toA = (a - ce); toA = toA / toA.Length;
                    Vector2d toB = (b - ce); toB = toB / toB.Length;
                    Vector2d bis = toA + toB; bis = bis / bis.Length;
                    bool bisOutward = bis.DotProduct(c - ce) <= 0; // 远离质心=外凸
                    Point2d arcMid = ce + (bisOutward ? bis : -bis) * r;
                    ed.WriteMessage($"\n  seg{i}: ARC  bulge={bulge:F4} R={r:F2} center=({ce.X:F2},{ce.Y:F2})");
                    ed.WriteMessage($" a=({a.X:F1},{a.Y:F1}) b=({b.X:F1},{b.Y:F1})");
                    ed.WriteMessage($" arcMid=({arcMid.X:F2},{arcMid.Y:F2}) outwardToCentroid={bisOutward}");
                }
            }
            idx++;
        }
        tr.Commit();
        ed.WriteMessage("\n");
    }

    /// <summary>按子类取标注关键坐标(尺寸界线起止+尺寸线位置)，基类 Dimension 无这些属性。</summary>
    private static string DescribeDim(Dimension d)
    {
        switch (d)
        {
            case RotatedDimension rd:
                return $"p1=({rd.XLine1Point.X:F1},{rd.XLine1Point.Y:F1}) p2=({rd.XLine2Point.X:F1},{rd.XLine2Point.Y:F1}) dimLine=({rd.DimLinePoint.X:F1},{rd.DimLinePoint.Y:F1})";
            case AlignedDimension ad:
                return $"p1=({ad.XLine1Point.X:F1},{ad.XLine1Point.Y:F1}) p2=({ad.XLine2Point.X:F1},{ad.XLine2Point.Y:F1}) dimLine=({ad.DimLinePoint.X:F1},{ad.DimLinePoint.Y:F1})";
            case RadialDimension rdd:
                return $"center=({rdd.Center.X:F1},{rdd.Center.Y:F1}) chord=({rdd.ChordPoint.X:F1},{rdd.ChordPoint.Y:F1}) leader={rdd.LeaderLength:F1}";
            case LineAngularDimension2 la:
                return $"v1=({la.XLine1Start.X:F1},{la.XLine1Start.Y:F1}) v2=({la.XLine2Start.X:F1},{la.XLine2Start.Y:F1}) arcPt=({la.ArcPoint.X:F1},{la.ArcPoint.Y:F1})";
            default:
                return "";
        }
    }

    private static Autodesk.AutoCAD.Geometry.Point2d DebugCentroid(Polyline pl)
    {
        double cx = 0, cy = 0, area = 0;
        int n = pl.NumberOfVertices;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            var pi = pl.GetPoint2dAt(i); var pj = pl.GetPoint2dAt(j);
            double cross = pi.X * pj.Y - pj.X * pi.Y;
            area += cross; cx += (pi.X + pj.X) * cross; cy += (pi.Y + pj.Y) * cross;
        }
        area *= 0.5;
        if (System.Math.Abs(area) < 1e-9) return pl.GetPoint2dAt(0);
        return new Autodesk.AutoCAD.Geometry.Point2d(cx / (6 * area), cy / (6 * area));
    }

    // ---- 私有辅助 ----

    /// <summary>删除 ADIM 和 ADIM_CENTER 图层上【带 AUTODIM XData 标记】且落在指定区域内的
    /// Dimension/Line。只删自己生成的、且只删本区域内的——用户手标在同一图层的不会被擦，
    /// 其它区域自己生成的也保留。region=null 时清全部带标记的(整图刷新)。
    /// buffer 取 5×baseGap，足以覆盖分层标注向外偏移的距离。</summary>
    private static void PurgeAdimEntities(Transaction tr, BlockTableRecord ms,
        string adimLayer, Extents3d? region, double buffer)
    {
        Extents3d? buf = null;
        if (region.HasValue)
        {
            var r = region.Value;
            buf = new Extents3d(
                new Point3d(r.MinPoint.X - buffer, r.MinPoint.Y - buffer, r.MinPoint.Z),
                new Point3d(r.MaxPoint.X + buffer, r.MaxPoint.Y + buffer, r.MaxPoint.Z));
        }
        var toErase = new List<ObjectId>();
        foreach (ObjectId id in ms)
        {
            Entity ent;
            try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; }
            catch { continue; }
            if (ent == null) continue;
            if (ent is not Dimension && ent is not Line) continue;
            if (ent.Layer != adimLayer && ent.Layer != "ADIM_CENTER") continue;
            // 只删自己生成的(带 AUTODIM 标记)——用户手标的保留
            if (!Cad.AdimMarker.IsMarked(ent)) continue;
            if (buf.HasValue)
            {
                Extents3d e;
                try { e = ent.GeometricExtents; }
                catch { toErase.Add(id); continue; }
                if (!Intersects2d(e, buf.Value)) continue;
            }
            toErase.Add(id);
        }
        foreach (var id in toErase)
        {
            if (tr.GetObject(id, OpenMode.ForWrite) is Entity ent) ent.Erase();
        }
    }

    private static bool Intersects2d(Extents3d a, Extents3d b) =>
        !(a.MaxPoint.X < b.MinPoint.X || a.MinPoint.X > b.MaxPoint.X ||
          a.MaxPoint.Y < b.MinPoint.Y || a.MinPoint.Y > b.MaxPoint.Y);

    private static void RunWithMode(TriggerMode mode)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        Editor ed = doc.Editor;

        ObjectId[]? ids = SelectionService.Acquire(ed, mode);
        if (ids == null) { ed.WriteMessage("\n未选择到有效对象。\n"); return; }

        RunEngine(doc, ids, new AutoDimOptions());
    }

    private static void RunEngine(Document doc, ObjectId[] ids, AutoDimOptions opt)
    {
        Database db = doc.Database;
        Editor ed = doc.Editor;
        DimResult r = default;

        try
        {
            // 整条命令一个事务 => AutoCAD 自动归并为单步 Undo。
            using Transaction tr = db.TransactionManager.StartTransaction();

            // 算包围盒(用于 DIMSCALE 自适应 + 各 dimensioner)
            Extents3d? extBox = Cad.GeometryUtils.CombinedExtents(tr, ids);

            // 从持久化配置读类别开关覆盖 opt.Categories
            int cats = Cad.OptionsStore.ReadCategories(db, tr);
            opt.Categories = (DimCategory)cats;

            ObjectId layerId = LayerHelper.EnsureLayer(db, tr, opt.LayerName);
            // 专属标注样式 ADIM(文字/箭头/比例固化)，不碰用户全局变量
            ObjectId dimStyleId = Cad.DimStyleSetup.EnsureStyle(db, tr, extBox);

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            // 重新标注前先清场：只清掉落在本次操作区域(带 buffer)内的旧标注，
            // 避免不同区域分别标注时互相擦除(extBox=null 时清全部，用于整图刷新)。
            double buf = extBox.HasValue ? 5.0 * GeometryUtils.AutoGap(extBox.Value) : 0.0;
            PurgeAdimEntities(tr, ms, opt.LayerName, extBox, buf);

            r = DimensionEngine.Run(db, tr, ms, ids, opt, dimStyleId, layerId, extBox);
            tr.Commit();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            ed.WriteMessage($"\nAUTODIM 出错: {ex.Message}\n");
            return;
        }

        // 统计行：既反馈用户，也供测试脚本 grep 断言。
        ed.WriteMessage(
            $"\nAUTODIM: overall={r.Overall} segment={r.Segment} arc={r.Arc} " +
            $"circle={r.Circle} position={r.Position} angular={r.Angular} total={r.Total} " +
            $"skipW={r.SkipW} skipH={r.SkipH} (layer={opt.LayerName})\n");
    }
}
