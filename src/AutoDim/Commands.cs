using Autodesk.AutoCAD.ApplicationServices;         // Document
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AutoDim.Cad;
using AutoDim.Config;
using AutoDim.Core;
using AutoDim.Core.Geometry;
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
            "\nAutoDim 已加载。命令: AUTODIM / ADIMALL / ADIMCOORD / ADIMCLEAN / ADIMCFG / ADIMVER\n");
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

        RunAutoOrClean(doc, ids);
    }

    /// <summary>整图自动标注。</summary>
    [CommandMethod("ADIMALL", CommandFlags.Modal)]
    public void AdimAll() => RunWithMode(TriggerMode.All);

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

    /// <summary>查看当前加载的插件版本与状态：DLL 路径/构建时间/AutoCAD 版本/配置开关，
    /// 用于判断 AutoCAD 里加载的是不是最新构建。</summary>
    [CommandMethod("ADIMVER", CommandFlags.Modal)]
    public void AdimVer()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        Editor ed = doc.Editor;
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var ver = asm.GetName().Version;
        string path = "";
        string buildTime = "";
        try
        {
            path = asm.Location;
            var fi = new System.IO.FileInfo(path);
            if (fi.Exists)
                buildTime = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch { }
        string acadVer = "";
        try { acadVer = AcApp.GetSystemVariable("ACADVER")?.ToString() ?? ""; }
        catch { }
        string cats = "";
          using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
          {
              int c = Cad.OptionsStore.ReadCategories(doc.Database, tr);
              cats = $"Overall={((Config.DimCategory)c).HasFlag(Config.DimCategory.Overall)} " +
                     $"Segment={((Config.DimCategory)c).HasFlag(Config.DimCategory.Segment)} " +
                     $"Holes={((Config.DimCategory)c).HasFlag(Config.DimCategory.Holes)}";
              tr.Commit();
          }
          ed.WriteMessage(
              $"\nAutoDim v{ver} (构建 {buildTime})\n" +
              $"  DLL: {path}\n" +
              $"  AutoCAD: {acadVer}\n" +
              $"  类别: {cats}\n");
    }

    /// <summary>图纸标注命令（原名"清洗"）：不做任何几何重建/绘制副本，
    /// 直接对原图实体标注——标注基准永远是原图上的真实顶点/圆心/半径。</summary>
    [CommandMethod("ADIMCLEAN", CommandFlags.UsePickSet | CommandFlags.Modal)]
    public void AdimClean()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        Editor ed = doc.Editor;

        ObjectId[]? ids = SelectionService.Acquire(ed, TriggerMode.Pickfirst)
                          ?? SelectionService.Acquire(ed, TriggerMode.All);
        if (ids == null || ids.Length == 0)
        {
            ed.WriteMessage("\n未选择到有效对象。\n");
            return;
        }
        // 顺手清除旧版本 ADIMCLEAN 曾绘制到 ADIM_CLEAN/_L 图层上的重建副本
        // （"抄一遍再标注"的造假中间层），避免原图残留假轮廓/假圆。
        PurgeCleanCopyLayers(doc.Database);
        GroupedDim(doc, ids);
    }

    /// <summary>按特征组对原图实体直接标注：闭合轮廓 + 圆按包围盒邻近聚成组，
    /// 逐组标注（纯孔组只标孔、混合组主面全标、小碎面只标总体、开放线独立标分段），
    /// 几何基准始终是原图实体本身的顶点/圆心/半径——不重建几何、不画任何副本。</summary>
    private static void GroupedDim(Document doc, ObjectId[] ids)
    {
        Editor ed = doc.Editor;
        Database db = doc.Database;

        var faceIds = new List<ObjectId>();   // 闭合 Polyline（轮廓面）
        var circleIds = new List<ObjectId>(); // Circle（孔）
        var openIds = new List<ObjectId>();   // Line / Arc / 开放 Polyline
        try
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    // ids 是命令开始时抓取的整图快照；PurgeCleanCopyLayers 可能已把
                    // 旧版残留的 ADIM_CLEAN 副本擦掉，读取这些已擦除对象会抛 eWasErased。
                    Entity? ent;
                    try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                    catch { continue; }
                    if (ent == null) continue;
                    if (ent is Dimension) continue;   // 插件自产标注不参与分组/统计（保证幂等）
                    if (IsAuxLayer(ent.Layer)) continue;
                    switch (ent)
                    {
                        case Polyline pl when pl.Closed:
                            faceIds.Add(id);
                            break;
                        case Circle:
                            circleIds.Add(id);
                            break;
                        case Line or Arc or Polyline:
                            openIds.Add(id);
                            break;
                    }
                }
                tr.Commit();
            }

            // 原实体几何（分组与面积判定用，事务内读取后转纯数据）
            var faces = new List<FaceData>();
            foreach (var id in faceIds)
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is Polyline pl && pl.Closed)
                    {
                        int n = pl.NumberOfVertices;
                        var pts = new Point2D[n];
                        var bulges = new double[n];
                        for (int i = 0; i < n; i++)
                        {
                            var p = pl.GetPoint2dAt(i);
                            pts[i] = new Point2D(p.X, p.Y);
                            bulges[i] = pl.GetBulgeAt(i);
                        }
                        faces.Add(new FaceData(pts, bulges));
                    }
                    tr.Commit();
                }
            }
            var circGeoms = new List<(Point2D Center, double Radius)>();
            foreach (var id in circleIds)
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is Circle c)
                        circGeoms.Add((new Point2D(c.Center.X, c.Center.Y), c.Radius));
                    tr.Commit();
                }
            }

            // 特征分组公差：按有效实体包围盒对角线比例（至少 2mm）。
            // 用收集后的有效 id（原始 ids 快照可能含被 PurgeCleanCopyLayers 擦掉的旧副本）。
            var validIds = faceIds.Concat(circleIds).Concat(openIds).ToArray();
            Extents3d? allExt = null;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                allExt = Cad.GeometryUtils.CombinedExtents(tr, validIds);
                tr.Commit();
            }
            double gapTol = 1.0;
            if (allExt.HasValue)
            {
                var e = allExt.Value;
                double diag = Math.Sqrt(
                    (e.MaxPoint.X - e.MinPoint.X) * (e.MaxPoint.X - e.MinPoint.X) +
                    (e.MaxPoint.Y - e.MinPoint.Y) * (e.MaxPoint.Y - e.MinPoint.Y));
                gapTol = Math.Max(1.0, diag * 0.001);
            }

            var groups = FeatureGrouping.GroupFeatures(faces, circGeoms, gapTol);
            ed.WriteMessage(
                $"\nADIMGROUP: 闭合面={faceIds.Count} 圆={circleIds.Count} 开放线={openIds.Count} " +
                $"特征组={groups.Count} (原图实体直接标注，无重建副本)\n");

            // 整区清场：只清落在本次区域内的自产旧标注
            if (allExt.HasValue)
            {
                using (Transaction trP = db.TransactionManager.StartTransaction())
                {
                    var btP = (BlockTable)trP.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var msP = (BlockTableRecord)trP.GetObject(btP[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    double bufP = 6.0 * Cad.GeometryUtils.AutoGap(allExt.Value);
                    PurgeAdimEntities(trP, msP, allExt, bufP);
                    trP.Commit();
                }
            }

            int dimmedGroups = 0, sumDims = 0;
            ed.WriteMessage($"    -> 按 {groups.Count} 个特征组分别标注...\n");
            // 纯孔组（无闭合轮廓）：统一累积到孔池一次标注——CircleDimensioner 内部
            // 会做直径分桶 + 空间聚类 + 镜像合并（N×Ød），避免每个孔被拆成独立组、
            // 直径合并与阵列注法全部失效（test3 16 孔曾被打散成 16 个单独 Ø）。
            var holePool = new List<ObjectId>();
            int holeGroupCount = 0;
            foreach (var g in groups)
            {
                var gIds = new List<ObjectId>();
                foreach (var fi in g.FaceIndices)
                    if (fi < faceIds.Count) gIds.Add(faceIds[fi]);
                foreach (var ci in g.CircleIndices)
                    if (ci < circleIds.Count) gIds.Add(circleIds[ci]);
                if (gIds.Count == 0) continue;

                // 组标注策略（沿用分组标注的组织逻辑，但实体全部是原图对象）：
                //   纯孔组       -> 累积到统一孔池（直径合并 N×Ød）
                //   全小碎面组   -> 最大面+孔：总体+孔（不标分段）
                //   混合组       -> 最大面+孔：全部；其余小面：只标总体
                if (g.FaceIndices.Count == 0)
                {
                    holePool.AddRange(g.CircleIndices
                        .Where(ci => ci < circleIds.Count)
                        .Select(ci => circleIds[ci]));
                    holeGroupCount++;
                    continue;
                }

                // 最大面 = 主面
                int mainFi = g.FaceIndices[0];
                double mainArea = -1;
                foreach (var fi in g.FaceIndices)
                {
                    if (fi >= faces.Count) continue;
                    double a = ContourExtractor.FaceArea(faces[fi].Points);
                    if (a > mainArea) { mainArea = a; mainFi = fi; }
                }
                var mainIds = new List<ObjectId>();
                if (mainFi < faceIds.Count) mainIds.Add(faceIds[mainFi]);
                foreach (var ci in g.CircleIndices)
                    if (ci < circleIds.Count) mainIds.Add(circleIds[ci]);

                bool allSmall = g.FaceIndices.All(fi => fi < faces.Count &&
                    ContourExtractor.FaceArea(faces[fi].Points) < 50.0);

                if (allSmall && mainIds.Count > 0)
                {
                    sumDims += RunEngine(doc, mainIds.ToArray(),
                        new AutoDimOptions { Categories = DimCategory.Overall | DimCategory.Holes },
                        usePersistedCategories: false, purge: false);
                    dimmedGroups++;
                }
                else if (!allSmall)
                {
                    if (mainIds.Count > 0)
                    {
                        sumDims += RunEngine(doc, mainIds.ToArray(),
                            new AutoDimOptions { Categories = DimCategory.All },
                            usePersistedCategories: false, purge: false);
                        dimmedGroups++;
                    }
                    // 其余小面：若与主面"连体"(包围盒重叠度高)跳过，否则只标总体
                    var otherIds = new List<ObjectId>();
                    foreach (var fi in g.FaceIndices)
                    {
                        if (fi == mainFi || fi >= faceIds.Count) continue;
                        otherIds.Add(faceIds[fi]);
                    }
                    if (otherIds.Count > 0)
                    {
                        using (Transaction trO = db.TransactionManager.StartTransaction())
                        {
                            Extents3d? mExt = Cad.GeometryUtils.CombinedExtents(trO, mainIds.ToArray());
                            Extents3d? oExt = Cad.GeometryUtils.CombinedExtents(trO, otherIds.ToArray());
                            double overlap = 0;
                            if (mExt.HasValue && oExt.HasValue)
                            {
                                var a = mExt.Value; var b = oExt.Value;
                                double ix = Math.Min(a.MaxPoint.X, b.MaxPoint.X) - Math.Max(a.MinPoint.X, b.MinPoint.X);
                                double iy = Math.Min(a.MaxPoint.Y, b.MaxPoint.Y) - Math.Max(a.MinPoint.Y, b.MinPoint.Y);
                                if (ix > 0 && iy > 0)
                                {
                                    double aw = a.MaxPoint.X - a.MinPoint.X, ah = a.MaxPoint.Y - a.MinPoint.Y;
                                    overlap = (ix * iy) / Math.Max(1e-9, aw * ah);
                                }
                            }
                            trO.Commit();
                            if (overlap < 0.6)
                            {
                                sumDims += RunEngine(doc, otherIds.ToArray(),
                                    new AutoDimOptions { Categories = DimCategory.Overall },
                                    usePersistedCategories: false, purge: false);
                                dimmedGroups++;
                            }
                        }
                    }
                }
            }

            // 统一孔池：所有散孔一次标注（内部自动合并同规格/对称/阵列）
            if (holePool.Count > 0)
            {
                sumDims += RunEngine(doc, holePool.ToArray(),
                    new AutoDimOptions { Categories = DimCategory.Holes },
                    usePersistedCategories: false, purge: false);
                dimmedGroups += holeGroupCount;
            }

            // 开放线段（不属于任何闭合轮廓/圆）：独立成组标 总体+分段——
            // 圆角矩形/腰形槽等 SW 导出轮廓常为独立 LINE+ARC（无闭合 Polyline），
            // 必须出总体长宽（界线取自贴边直线段中点/弧极值点，不悬空）。
            if (openIds.Count > 0)
            {
                sumDims += RunEngine(doc, openIds.ToArray(),
                    new AutoDimOptions { Categories = DimCategory.Segment | DimCategory.Overall },
                    usePersistedCategories: false, purge: false);
                dimmedGroups++;
            }

            // 布局收尾：整区去重 + 外推避让
            if (allExt.HasValue)
            {
                int removedDup = 0, movedAway = 0;
                using (Transaction trL = db.TransactionManager.StartTransaction())
                {
                    double gapL = Cad.GeometryUtils.AutoGap(allExt.Value);
                    (removedDup, movedAway) = Dimensioning.LayoutSolver.Resolve(db, trL, allExt.Value, gapL);
                    trL.Commit();
                }
                if (removedDup > 0 || movedAway > 0)
                    ed.WriteMessage($"    -> 布局收尾: 去重 {removedDup} 个 / 外推避让 {movedAway} 次\n");
            }
            else
            {
                using (Transaction trL = db.TransactionManager.StartTransaction())
                {
                    Dimensioning.LayoutSolver.Dedupe(db, trL);
                    trL.Commit();
                }
            }

            var (finalDims, finalNotes) = CountAdimEntities(db);
            var (overlaps, overlapTop) = CountDimOverlaps(db);
            var (textHits, textTop) = CountDimTextOverlaps(db);
            ed.WriteMessage(
                $"    -> 合计: {dimmedGroups} 组 / 生成 {sumDims} 个尺寸、落地 {finalDims} 个尺寸 " +
                $"+ {finalNotes} 个注记 / ADIM 文字撞车 {textHits} 对 " +
                $"[AABB 重叠 {overlaps}] [{overlapTop}] " +
                $"(landed={finalDims} notes={finalNotes} groups={dimmedGroups} textHits={textHits})\n");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            ed.WriteMessage($"\nADIMCLEAN 出错: {ex.Message}\n");
        }
    }

    /// <summary>辅助线图层默认不参与轮廓提取（中心线/虚线/点划线等）。</summary>
    private static bool IsAuxLayer(string layer)
    {
        if (layer is "ADIM" or "ADIM_CLEAN" or "ADIM_CLEAN_L" or "ADIM_CENTER" or "Defpoints") return true;
        return layer.Contains("中心线") || layer.Contains("虚线") || layer.Contains("点划线");
    }

    /// <summary>删除 ADIM_CLEAN / ADIM_CLEAN_L 图层上的全部实体：旧版本清洗管线
    /// 会把重建的面/圆/线画到这两个图层（"抄一遍再标注"的造假根源），
    /// 新版本不再绘制任何副本，运行 ADIMCLEAN 时顺手清掉历史残留。</summary>
    private static void PurgeCleanCopyLayers(Database db)
    {
        try
        {
            using Transaction tr = db.TransactionManager.StartTransaction();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            foreach (ObjectId id in ms)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent) continue;
                if (ent.Layer is "ADIM_CLEAN" or "ADIM_CLEAN_L")
                    tr.GetObject(id, OpenMode.ForWrite).Erase();
            }
            tr.Commit();
        }
        catch { }
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
                ObjectId layerId = db.Clayer;      // 当前图层(CAD 模板)
                ObjectId dimStyleId = db.Dimstyle; // 当前标注样式(CAD 模板)
            double baseGap = extBox != null ? GeometryUtils.AutoGap(extBox.Value) : 10.0;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            PurgeAdimEntities(tr, ms, extBox, 0);

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

        var kw = new PromptKeywordOptions(
            "\n设置 [总体(O)/分段(E)/孔圆(H)/全部(ALL)/清空(NONE)]: ");
        kw.Keywords.Add("O"); kw.Keywords.Add("E"); kw.Keywords.Add("H");
        kw.Keywords.Add("ALL"); kw.Keywords.Add("NONE");
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
        string curStyleName = "?";
        try
        {
            using Transaction trS = db.TransactionManager.StartTransaction();
            var dst = (DimStyleTable)trS.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            if (dst.Has(db.Dimstyle))
            {
                var rec = (DimStyleTableRecord)trS.GetObject(db.Dimstyle, OpenMode.ForRead);
                curStyleName = rec.Name;
            }
            trS.Commit();
        }
        catch { }
        ed.WriteMessage($"\n  当前标注样式 = {curStyleName}（标注全部使用它）");

        ed.WriteMessage("\n== 图中 Dimension 实体 ==");
        using Transaction tr = db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        int k = 0;
        foreach (ObjectId id in ms)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Dimension d) continue;
            string sty = d.DimensionStyle.ToString();
            string geo = DescribeDim(d);
            string ext = "";
            try
            {
                var g = d.GeometricExtents;
                ext = $" ext=({g.MinPoint.X:F1},{g.MinPoint.Y:F1})-({g.MaxPoint.X:F1},{g.MaxPoint.Y:F1})";
            }
            catch { }
            ed.WriteMessage($"\n  [{k}] {d.GetType().Name} text=\"{d.DimensionText}\" style={sty} {geo}{ext} txtRot={d.TextRotation:F2} layer={d.Layer}");
            k++;
        }
        tr.Commit();
        ed.WriteMessage("\n");
    }

    // ---- 私有辅助 ----

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

    /// <summary>删除【带 AUTODIM XData 标记】且落在指定区域内的 Dimension/Line/MText。
    /// 标注现在画在用户当前图层(跟随 CAD 模板)，不能再按图层过滤——按标记精确识别
    /// 自己生成的实体，用户手标永远不会被擦；region=null 时清全部带标记的(整图刷新)。
    /// buffer 取 5×baseGap，足以覆盖分层标注向外偏移的距离。</summary>
    private static void PurgeAdimEntities(Transaction tr, BlockTableRecord ms,
        Extents3d? region, double buffer)
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
            Entity? ent;
            try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; }
            catch { continue; }
            if (ent == null) continue;
            if (ent is not Dimension && ent is not Line && ent is not MText) continue;
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

        RunAutoOrClean(doc, ids);
    }

    /// <summary>统一入口：直接对原图实体标注（清洗/重建/画副本管线已废弃——
    /// 标注基准永远是原图上的真实顶点/圆心/半径）。</summary>
    private static void RunAutoOrClean(Document doc, ObjectId[] ids)
    {
        RunEngine(doc, ids, new AutoDimOptions());
    }

    private static int RunEngine(Document doc, ObjectId[] ids, AutoDimOptions opt,
                                 bool usePersistedCategories = true, bool purge = true)
    {
        Database db = doc.Database;
        Editor ed = doc.Editor;
        DimResult r = default;
        string layerName = "?";

        try
        {
            // 整条命令一个事务 => AutoCAD 自动归并为单步 Undo。
            using Transaction tr = db.TransactionManager.StartTransaction();

            // 算包围盒(用于 DIMSCALE 自适应 + 各 dimensioner)
            Extents3d? extBox = Cad.GeometryUtils.CombinedExtents(tr, ids);

            // 从持久化配置读类别开关覆盖 opt.Categories（除非调用方明确不读）
            if (usePersistedCategories)
            {
                int cats = Cad.OptionsStore.ReadCategories(db, tr);
                opt.Categories = (DimCategory)cats;
            }

              // 标注只用当前图纸的标注样式+当前图层(CAD 模板)：样板里调什么就用什么
              ObjectId layerId = db.Clayer;
              ObjectId dimStyleId = db.Dimstyle;
              try { layerName = (tr.GetObject(db.Clayer, OpenMode.ForRead) as LayerTableRecord)?.Name ?? "?"; }
              catch { }

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            // 重新标注前先清场：只清掉落在本次操作区域(带 buffer)内的旧标注，
            // 避免不同区域分别标注时互相擦除(extBox=null 时清全部，用于整图刷新)。
            // purge=false：同组内后续调用（如"其余小面"）不 purge，避免误擦本组主面刚生成的尺寸。
            if (purge)
            {
                double buf = extBox.HasValue ? 5.0 * GeometryUtils.AutoGap(extBox.Value) : 0.0;
                PurgeAdimEntities(tr, ms, extBox, buf);
            }

            r = DimensionEngine.Run(db, tr, ms, ids, opt, dimStyleId, layerId, extBox);
            tr.Commit();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            ed.WriteMessage($"\nAUTODIM 出错: {ex.Message}\n");
            return 0;
        }

        // 统计行：既反馈用户，也供测试脚本 grep 断言。
        ed.WriteMessage(
            $"\nAUTODIM: overall={r.Overall} segment={r.Segment} arc={r.Arc} " +
            $"circle={r.Circle} position={r.Position} angular={r.Angular} total={r.Total} " +
            $"skipW={r.SkipW} skipH={r.SkipH} (layer={layerName})\n");
        return r.Total;
    }

    /// <summary>统计 ADIM 图层 Dimension 外接框之间的重叠对数（布局质量信号）。</summary>
    private static (int Count, string Top) CountDimOverlaps(Database db)
    {
        var boxes = new List<Extents3d>();
        var types = new List<string>();
        var radialCenters = new List<Point3d?>();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                  if (tr.GetObject(id, OpenMode.ForRead) is not Dimension d) continue;
                  if (!Cad.AdimMarker.IsMarked(d)) continue;   // 只统计插件生成的标注
                try
                {
                    boxes.Add(d.GeometricExtents);
                    types.Add(d.GetType().Name);
                    radialCenters.Add(d is RadialDimension rd ? (Point3d?)rd.Center : null);
                }
                catch { }
            }
            tr.Commit();
        }
        int cnt = 0;
        var pairCounts = new Dictionary<(string, string), int>();
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
            {
                if (!BoxesOverlapArea(boxes[i], boxes[j])) continue;
                // 排除"同一孔的直径线与其定位延伸线在孔心处相交"的假阳性：
                // Radial 圆心落在另一标注框内视为同孔内部接触
                if (radialCenters[i] is Point3d ci && BoxContains(boxes[j], ci)) continue;
                if (radialCenters[j] is Point3d cj && BoxContains(boxes[i], cj)) continue;
                cnt++;
                var key = string.CompareOrdinal(types[i], types[j]) <= 0
                    ? (types[i], types[j]) : (types[j], types[i]);
                pairCounts[key] = pairCounts.GetValueOrDefault(key) + 1;
            }
        var top = pairCounts.OrderByDescending(kv => kv.Value).Take(6)
                            .Select(kv => $"{kv.Key.Item1}+{kv.Key.Item2}:{kv.Value}");
        return (cnt, string.Join(" ", top));
    }

    private static bool BoxContains(Extents3d box, Point3d p) =>
        p.X >= box.MinPoint.X && p.X <= box.MaxPoint.X &&
        p.Y >= box.MinPoint.Y && p.Y <= box.MaxPoint.Y;

    /// <summary>统计 ADIM 图层最终落地的 Dimension 数与 MText 注记数。</summary>
    private static (int dims, int notes) CountAdimEntities(Database db)
    {
        int dims = 0, notes = 0;
        using Transaction tr = db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        foreach (ObjectId id in ms)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent) continue;
            if (!Cad.AdimMarker.IsMarked(ent)) continue;   // 只统计插件生成的标注
            if (ent is Dimension) dims++;
            else if (ent is MText) notes++;
        }
        tr.Commit();
        return (dims, notes);
    }

    /// <summary>统计 ADIM 图层标注【文字实体】之间的真实撞车对数（可读性指标）。
    /// 尺寸链/投影的 AABB 常互相交叉但图形不接触，用文字框更接近"看起来乱不乱"。</summary>
    private static (int Count, string Top) CountDimTextOverlaps(Database db)
    {
        var boxes = new List<Extents3d?>();
        var types = new List<string>();
        var radialCenters = new List<Point3d?>();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent) continue;
                if (!Cad.AdimMarker.IsMarked(ent)) continue;   // 只统计插件生成的标注
                if (ent is Dimension d)
                {
                    boxes.Add(DimTextExtents(tr, d));
                    types.Add(d.GetType().Name);
                    radialCenters.Add(d is RadialDimension rd ? (Point3d?)rd.Center : null);
                }
                else if (ent is MText mt)
                {
                    try { boxes.Add(mt.GeometricExtents); }
                    catch { boxes.Add(null); }
                    types.Add("CountNote");
                    radialCenters.Add(null);
                }
            }
            tr.Commit();
        }
        int cnt = 0;
        var pairCounts = new Dictionary<(string, string), int>();
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
            {
                if (boxes[i] == null || boxes[j] == null) continue;
                var a = boxes[i]!.Value; var b = boxes[j]!.Value;
                double ix = System.Math.Min(a.MaxPoint.X, b.MaxPoint.X) - System.Math.Max(a.MinPoint.X, b.MinPoint.X);
                double iy = System.Math.Min(a.MaxPoint.Y, b.MaxPoint.Y) - System.Math.Max(a.MinPoint.Y, b.MinPoint.Y);
                if (ix <= 0.05 || iy <= 0.05) continue;
                // 同一孔的直径文字与定位延伸线不算撞车（AABB 层面的既有排除规则）
                if (radialCenters[i] is Point3d ci && BoxContains(b, ci)) continue;
                if (radialCenters[j] is Point3d cj && BoxContains(a, cj)) continue;
                cnt++;
                var key = string.CompareOrdinal(types[i], types[j]) <= 0
                    ? (types[i], types[j]) : (types[j], types[i]);
                pairCounts[key] = pairCounts.GetValueOrDefault(key) + 1;
            }
        var top = pairCounts.OrderByDescending(kv => kv.Value).Take(6)
                            .Select(kv => $"{kv.Key.Item1}+{kv.Key.Item2}:{kv.Value}");
        return (cnt, string.Join(" ", top));
    }

    /// <summary>取标注块里的文字实体(WCS)外接框；找不到返回 null。</summary>
    private static Extents3d? DimTextExtents(Transaction tr, Dimension d)
    {
        try
        {
            // 优先用 Dimension 自带文字位置与尺寸(DimBlockId 才是标注匿名块；BlockId 是父级块引用)
            var pos = d.TextPosition;
            var size = d.TextDefinedSize;
            if (size.X > 1e-9 && size.Y > 1e-9)
            {
                return new Extents3d(
                    new Point3d(pos.X - size.X * 0.5, pos.Y - size.Y * 0.5, 0),
                    new Point3d(pos.X + size.X * 0.5, pos.Y + size.Y * 0.5, 0));
            }
            if (tr.GetObject(d.DimBlockId, OpenMode.ForRead) is not BlockTableRecord blk) return null;
            foreach (ObjectId bid in blk)
            {
                if (tr.GetObject(bid, OpenMode.ForRead) is DBText txt)
                    return txt.GeometricExtents;
                if (tr.GetObject(bid, OpenMode.ForRead) is MText mt)
                    return mt.GeometricExtents;
            }
        }
        catch { }
        return null;
    }

    /// <summary>仅当两个外接框有正面积交集(而非共享边/点)才算重叠。</summary>
    private static bool BoxesOverlapArea(Extents3d a, Extents3d b, double eps = 0.01)
    {
        double ix = Math.Min(a.MaxPoint.X, b.MaxPoint.X) - Math.Max(a.MinPoint.X, b.MinPoint.X);
        double iy = Math.Min(a.MaxPoint.Y, b.MaxPoint.Y) - Math.Max(a.MinPoint.Y, b.MinPoint.Y);
        return ix > eps && iy > eps;
    }
}
