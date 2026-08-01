using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoDim.Cad;

namespace AutoDim.Dimensioning;

/// <summary>
/// ② 轮廓各段尺寸（GB 机械制图惯例）：
///   水平/垂直边 -> AlignedDimension(边长)，朝轮廓外侧偏移。
///   斜边        -> 智能判断：L 干净则标斜边长+夹角；L 脏而 dx/dy 干净则标水平投影+垂直投影+夹角；
///                 否则默认标斜边长(2位小数)+夹角。夹角标斜边与水平基准之角。
///   圆弧段      -> RadialDimension 标 R，从圆心穿过"弧中点"向外引线（端点角平分线定弧中点，
///                 保证指向有弧的一侧，而非虚空）。
/// 放在内层（靠近轮廓），不与总体/定位尺寸冲突。
/// </summary>
internal static class OutlineDimensioner
{
    private const double Tol = 1e-6;
    private const int MaxSegDims = 16;   // 每面最多标的分段数（优先长边，控制复杂边界尺寸线互压）
    private const int MaxArcDims = 8;    // 每面最多标的圆弧 R 数（优先大半径；小圆角通常"未注圆角"）

    /// <returns>(直线段标注数, 圆弧段标注数)</returns>
    public static (int segments, int arcs) Annotate(
        Database db, Transaction tr, BlockTableRecord space,
        IReadOnlyList<ObjectId> ids, ObjectId dimStyleId, ObjectId layerId, double baseGap,
        Extents3d? ext = null)
    {
        int seg = 0, arc = 0;
        // 短碎边不标：相对该轮廓尺度自适应（0.35×偏移量），下限 2mm——
        // 过滤矢量化噪声的亚毫米/毫米级碎段（test.dwg 的 1.25mm 级分段即此类）
        double minSegLen = System.Math.Max(2.0, baseGap * 0.35);
        // 跨面去重：同一几何边被两个相邻面共享(槽边/端头弧)时会各标一次，
        // 组内只标一次(国标不重复标注共享边)
        var seenSeg = new HashSet<(Point2d, Point2d)>();
        var seenArc = new HashSet<(Point2d, Point2d)>();

        foreach (var id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Polyline pl) continue;
            if (pl.NumberOfVertices < 2) continue;

            Point2d centroid = PolylineCentroid(pl);
            int n = pl.NumberOfVertices;

            // 直线段：先收集候选（跳过最外整边与短碎边），按长度排序，最多取 MaxSegDims 条
            var cands = new List<(int I, double Len)>();
            for (int i = 0; i < n; i++)
            {
                if (!pl.Closed && i == n - 1) break;
                int j = (i + 1) % n;
                if (ext != null && IsOutermostEdge(pl, i, j, ext.Value))
                    continue;
                if (System.Math.Abs(pl.GetBulgeAt(i)) >= 1e-9) continue;
                double len = (pl.GetPoint2dAt(j) - pl.GetPoint2dAt(i)).Length;
                if (len < minSegLen) continue;
                // 同一边反向/跨面共享只标一次(退化面与相邻共享面都会产生重复边)
                var a = pl.GetPoint2dAt(i);
                var b = pl.GetPoint2dAt(j);
                var key = a.X < b.X || (a.X == b.X && a.Y <= b.Y) ? (a, b) : (b, a);
                if (!seenSeg.Add(key)) continue;
                cands.Add((i, len));
            }
            cands.Sort((x, y) => y.Len.CompareTo(x.Len));
            if (cands.Count > MaxSegDims)
                cands = cands.GetRange(0, MaxSegDims);
            foreach (var (i, _) in cands)
                seg += AnnotateSegment(db, tr, space, pl.GetPoint2dAt(i), pl.GetPoint2dAt((i + 1) % n),
                                       centroid, dimStyleId, layerId, baseGap);

            // 圆弧段：先收集，按"圆心+半径"去重(同一圆被拆成多段弧时只标一次)，
            // 再按半径从大到小取前 MaxArcDims 条（R 引线是重叠大户，小圆角不逐个标）
            var arcCands = new List<(int I, double R, Point2d C)>();
            for (int i = 0; i < n; i++)
            {
                if (!pl.Closed && i == n - 1) break;
                if (System.Math.Abs(pl.GetBulgeAt(i)) < 1e-9) continue;
                var aa = pl.GetPoint2dAt(i);
                var bb = pl.GetPoint2dAt((i + 1) % n);
                var ak = aa.X < bb.X || (aa.X == bb.X && aa.Y <= bb.Y) ? (aa, bb) : (bb, aa);
                if (!seenArc.Add(ak)) continue;   // 共享弧(相邻面)只标一次
                var arcSeg = pl.GetArcSegment2dAt(i);
                arcCands.Add((i, arcSeg.Radius, arcSeg.Center));
            }
            var seenCircle = new HashSet<(int, int, int)>();
            var dedupArcs = new List<(int I, double R, Point2d C)>();
            foreach (var ac in arcCands.OrderByDescending(x => x.R))
            {
                var ck = ((int)System.Math.Round(ac.C.X * 10),
                          (int)System.Math.Round(ac.C.Y * 10),
                          (int)System.Math.Round(ac.R * 10));
                if (!seenCircle.Add(ck)) continue;  // 同一圆的多段弧只标一次
                dedupArcs.Add(ac);
            }
            // 残弧过滤：聚类/吸附可能产生 r<0.5mm 的微弧（扫描噪声），R 标注无意义
            dedupArcs = dedupArcs.Where(x => x.R >= 0.5).ToList();
            if (dedupArcs.Count > MaxArcDims)
                dedupArcs = dedupArcs.GetRange(0, MaxArcDims);
            // 同半径圆角(不同位置，如 4 个角都是 R5)≥3 个：只标一个代表 + "N×R" 注记，
            // 避免重复标一排相同的 R(GB 圆角阵列注法)
            var byRadius = new Dictionary<int, List<(int I, double R, Point2d C)>>();
            foreach (var ac in dedupArcs)
            {
                int rk = (int)System.Math.Round(ac.R * 10);
                if (!byRadius.TryGetValue(rk, out var l))
                    byRadius[rk] = l = new List<(int, double, Point2d)>();
                l.Add(ac);
            }
            foreach (var grp in byRadius.Values)
            {
                if (grp.Count >= 3)
                {
                    var rep = grp[0];
                    int j = (rep.I + 1) % n;
                    AnnotateArc(db, tr, space, pl, rep.I, j, centroid, dimStyleId, layerId, baseGap, ext,
                                $"{grp.Count}×");
                    arc++;
                }
                else
                {
                    foreach (var ac in grp)
                    {
                        int j = (ac.I + 1) % n;
                        AnnotateArc(db, tr, space, pl, ac.I, j, centroid, dimStyleId, layerId, baseGap, ext);
                        arc++;
                    }
                }
            }
        }

        // 独立 Line（用户用 LINE 画的开放轮廓/散线——test4 全是这种）：逐条标长度，
        // 共用 seenSeg 去重(与面共享边不重复标)；只取最长的 MaxSegDims 条防止碎线刷屏。
        var lineCands = new List<(Point2d A, Point2d B, double Len)>();
        foreach (var id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Line ln) continue;
            var la = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
            var lb = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
            double len = (lb - la).Length;
            if (len < minSegLen) continue;
            // 贴包围盒边的整宽/整高直线（如普通矩形的底边）跳过：总体尺寸已标该值，
            // 再标分段就是重复（与 Polyline 的 IsOutermostEdge 行为一致）。
            if (ext != null && IsOutermostLine(la, lb, ext.Value)) continue;
            var key = la.X < lb.X || (la.X == lb.X && la.Y <= lb.Y) ? (la, lb) : (lb, la);
            if (!seenSeg.Add(key)) continue;
            lineCands.Add((la, lb, len));
        }
        lineCands.Sort((x, y) => y.Len.CompareTo(x.Len));
        if (lineCands.Count > MaxSegDims)
            lineCands = lineCands.GetRange(0, MaxSegDims);
        Point2d gCentroid = ext.HasValue
            ? new Point2d((ext.Value.MinPoint.X + ext.Value.MaxPoint.X) * 0.5,
                          (ext.Value.MinPoint.Y + ext.Value.MaxPoint.Y) * 0.5)
            : new Point2d(0, 0);
        foreach (var (la, lb, _) in lineCands)
            seg += AnnotateSegment(db, tr, space, la, lb, gCentroid, dimStyleId, layerId, baseGap);

        // 独立 Arc 实体（圆角/腰形槽端头：SW 导出常为独立 ARC 而非 Polyline bulge）：
        // 与 Polyline 弧段共用端点去重，半径过滤 >=0.5，最多 MaxArcDims 条，
        // 同半径 ≥3 合并为 "N×R"（国标圆角阵列注法）。
        var arcEnts = new List<(Point2d A, Point2d B, Point2d C, double R)>();
        foreach (var id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Arc ar) continue;
            double r = ar.Radius;
            if (r < 0.5) continue;
            double a0 = ar.StartAngle, a1 = ar.EndAngle;
            if (a1 <= a0) a1 += 2.0 * System.Math.PI;
            var c = new Point2d(ar.Center.X, ar.Center.Y);
            var pa = new Point2d(c.X + r * System.Math.Cos(a0), c.Y + r * System.Math.Sin(a0));
            var pb = new Point2d(c.X + r * System.Math.Cos(a1), c.Y + r * System.Math.Sin(a1));
            // 端点吸附到 0.05 网格后去重（与 Polyline 弧端点可能差几个微米）
            var ka = new Point2d(System.Math.Round(pa.X * 20) / 20, System.Math.Round(pa.Y * 20) / 20);
            var kb = new Point2d(System.Math.Round(pb.X * 20) / 20, System.Math.Round(pb.Y * 20) / 20);
            var key = ka.X < kb.X || (ka.X == kb.X && ka.Y <= kb.Y) ? (ka, kb) : (kb, ka);
            if (!seenArc.Add(key)) continue;   // 与 Polyline 弧段共享边只标一次
            arcEnts.Add((pa, pb, c, r));
        }
        if (arcEnts.Count > 0)
        {
            arcEnts.Sort((x, y) => y.R.CompareTo(x.R));
            if (arcEnts.Count > MaxArcDims)
                arcEnts = arcEnts.GetRange(0, MaxArcDims);
            var byRadius = new Dictionary<int, List<(Point2d A, Point2d B, Point2d C, double R)>>();
            foreach (var ac in arcEnts)
            {
                int rk = (int)System.Math.Round(ac.R * 10);
                if (!byRadius.TryGetValue(rk, out var l))
                    byRadius[rk] = l = new List<(Point2d, Point2d, Point2d, double)>();
                l.Add(ac);
            }
            foreach (var grp in byRadius.Values)
            {
                if (grp.Count >= 3)
                {
                    var rep = grp[0];
                    AnnotateStandaloneArc(db, tr, space, rep.A, rep.B, rep.C, rep.R,
                                          dimStyleId, layerId, baseGap, ext, $"{grp.Count}×");
                    arc++;
                }
                else
                {
                    foreach (var ac in grp)
                    {
                        AnnotateStandaloneArc(db, tr, space, ac.A, ac.B, ac.C, ac.R,
                                              dimStyleId, layerId, baseGap, ext);
                        arc++;
                    }
                }
            }
        }
        return (seg, arc);
    }

    /// <summary>独立 Line 是否恰好贴包围盒外缘整宽或整高（同 Polyline 版 IsOutermostEdge）。</summary>
    private static bool IsOutermostLine(Point2d a, Point2d b, Extents3d ext)
    {
        double minX = ext.MinPoint.X, maxX = ext.MaxPoint.X;
        double minY = ext.MinPoint.Y, maxY = ext.MaxPoint.Y;
        const double tol = 1e-6;
        if (System.Math.Abs(a.Y - b.Y) < tol)
        {
            bool atBottom = System.Math.Abs(a.Y - minY) < tol, atTop = System.Math.Abs(a.Y - maxY) < tol;
            if (atBottom || atTop)
            {
                double lo = System.Math.Min(a.X, b.X), hi = System.Math.Max(a.X, b.X);
                if (System.Math.Abs(lo - minX) < tol && System.Math.Abs(hi - maxX) < tol) return true;
            }
        }
        if (System.Math.Abs(a.X - b.X) < tol)
        {
            bool atLeft = System.Math.Abs(a.X - minX) < tol, atRight = System.Math.Abs(a.X - maxX) < tol;
            if (atLeft || atRight)
            {
                double lo = System.Math.Min(a.Y, b.Y), hi = System.Math.Max(a.Y, b.Y);
                if (System.Math.Abs(lo - minY) < tol && System.Math.Abs(hi - maxY) < tol) return true;
            }
        }
        return false;
    }

    /// <summary>该直线段是否恰好贴包围盒外缘整宽或整高(底/顶整宽边、左/右整高边)。
    /// 这种边若再标会与其它分段拼成的总体形成封闭链，应跳过。</summary>
    private static bool IsOutermostEdge(Polyline pl, int i, int j, Extents3d ext)
    {
        double minX = ext.MinPoint.X, maxX = ext.MaxPoint.X;
        double minY = ext.MinPoint.Y, maxY = ext.MaxPoint.Y;
        const double tol = 1e-6;
        Point2d a = pl.GetPoint2dAt(i), b = pl.GetPoint2dAt(j);
        // 水平段且贴底/贴顶且端点横跨 minX..maxX(整宽)
        if (System.Math.Abs(a.Y - b.Y) < tol)
        {
            bool atBottom = System.Math.Abs(a.Y - minY) < tol, atTop = System.Math.Abs(a.Y - maxY) < tol;
            if (atBottom || atTop)
            {
                double lo = System.Math.Min(a.X, b.X), hi = System.Math.Max(a.X, b.X);
                if (System.Math.Abs(lo - minX) < tol && System.Math.Abs(hi - maxX) < tol) return true;
            }
        }
        // 垂直段且贴左/贴右且端点纵跨 minY..maxY(整高)
        if (System.Math.Abs(a.X - b.X) < tol)
        {
            bool atLeft = System.Math.Abs(a.X - minX) < tol, atRight = System.Math.Abs(a.X - maxX) < tol;
            if (atLeft || atRight)
            {
                double lo = System.Math.Min(a.Y, b.Y), hi = System.Math.Max(a.Y, b.Y);
                if (System.Math.Abs(lo - minY) < tol && System.Math.Abs(hi - maxY) < tol) return true;
            }
        }
        return false;
    }

    // ---------- 直线段（含斜边智能判断） ----------

    private static int AnnotateSegment(Database db, Transaction tr, BlockTableRecord space,
        Point2d a, Point2d b, Point2d centroid, ObjectId dimStyleId, ObjectId layerId, double baseGap)
    {
        Vector2d d = b - a;
        double dx = System.Math.Abs(d.X), dy = System.Math.Abs(d.Y);

        // 水平或垂直边：直接标边长(强制消零)
        if (dx < Tol || dy < Tol)
        {
            AddAligned(db, tr, space, a, b, centroid, dimStyleId, layerId, baseGap, FormatLen((b - a).Length));
            return 1;
        }

        // 斜边：直角三角形，自由度=2。GB/T 4458.4：斜边已由直角坐标(dx,dy)定义时不得标角度
        // (角度冗余+四舍五入致数值矛盾)。但"水平投影+垂直投影"双投影的尺寸界线盒在角部
        // 必然相交（实测是 Rotated+Rotated 重叠最大来源），改为只标较大的那个直角边(主投影)，
        // 另一坐标由闭合尺寸链隐含；绝不标夹角。
        double dxV = d.X, dyV = d.Y;
        double dxAbs = System.Math.Abs(dxV), dyAbs = System.Math.Abs(dyV);
        AddDominantProjection(db, tr, space, a, b, centroid, dxAbs, dyAbs, dimStyleId, layerId, baseGap);
        return 1;
    }

    /// <summary>沿边方向 AlignedDimension，朝外侧偏移；text 传 null 则用默认(显示测量值)。</summary>
    private static void AddAligned(Database db, Transaction tr, BlockTableRecord space,
        Point2d a, Point2d b, Point2d centroid, ObjectId dimStyleId, ObjectId layerId, double baseGap, string? text)
    {
        Vector2d d = b - a;
        double len = d.Length;
        if (len < Tol) return;
        Vector2d n2 = new Vector2d(-d.Y, d.X) / len;   // 左法线
        Point2d mid = a + d * 0.5;
        if (n2.DotProduct(centroid - mid) > 0) n2 = -n2;   // 翻到外侧
        // 内层(最靠近零件)：0.5×baseGap。定位尺寸要进一步外推到 2.0×baseGap，
        // 否则 120 这类分段尺寸线会落在 30/65 定位文字区内(穿字)。
        Point2d dl2 = mid + n2 * (0.8 * baseGap);

        var dim = new AlignedDimension(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0),
                                       new Point3d(dl2.X, dl2.Y, 0), "", dimStyleId);
        DimUtil.Append(db, tr, space, dim, dimStyleId, layerId, text);
    }

    /// <summary>斜边的单一主投影 RotatedDimension(模式 A')：
    /// 浅斜边(dx≥dy)标水平投影(1.0×baseGap)、陡斜边标垂直投影(2.0×baseGap)，
    /// 保持原双投影时代的排布距离，避免靠近轮廓与分段/径向标注互压。
    /// 垂直尺寸文字强制贴尺寸线左侧(TextRotation=-90°，GB 垂直尺寸惯例)。</summary>
    private static void AddDominantProjection(Database db, Transaction tr, BlockTableRecord space,
        Point2d a, Point2d b, Point2d centroid, double dx, double dy,
        ObjectId dimStyleId, ObjectId layerId, double baseGap)
    {
        Vector2d d = b - a;
        Vector2d n2 = new Vector2d(-d.Y, d.X) / d.Length;
        Point2d mid = a + d * 0.5;
        if (n2.DotProduct(centroid - mid) > 0) n2 = -n2;   // 翻到外侧

        if (dx >= dy)
        {
            // 水平投影(浅斜边主投影)：尺寸线水平，y 放斜边外侧 1.0×baseGap
            double yH = n2.Y > 0
                ? System.Math.Max(a.Y, b.Y) + 1.0 * baseGap
                : System.Math.Min(a.Y, b.Y) - 1.0 * baseGap;
            var dimH = new RotatedDimension(0.0,
                new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0),
                new Point3d((a.X + b.X) * 0.5, yH, 0), "", dimStyleId);
            DimUtil.Append(db, tr, space, dimH, dimStyleId, layerId, FormatLen(dx));
        }
        else
        {
            // 垂直投影(陡斜边主投影)：尺寸线垂直，x 放斜边外侧 2.0×baseGap
            double xV = n2.X > 0
                ? System.Math.Max(a.X, b.X) + 2.0 * baseGap
                : System.Math.Min(a.X, b.X) - 2.0 * baseGap;
            var dimV = new RotatedDimension(System.Math.PI * 0.5,
                new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0),
                new Point3d(xV, (a.Y + b.Y) * 0.5, 0), "", dimStyleId);
            DimUtil.Append(db, tr, space, dimV, dimStyleId, layerId, FormatLen(dy));
            // 垂直尺寸文字贴尺寸线左侧、字头朝左(GB 垂直尺寸惯例)，避免文字骑在尺寸线上被穿过
            dimV.TextRotation = -System.Math.PI * 0.5;
            dimV.RecomputeDimensionBlock(true);
        }
    }

    // ---------- 圆弧段（引线从圆心穿过弧中点向外，朝向远离邻边交点） ----------

    private static void AnnotateArc(Database db, Transaction tr, BlockTableRecord space,
        Polyline pl, int i, int j, Point2d centroid, ObjectId dimStyleId, ObjectId layerId, double baseGap,
        Extents3d? ext = null, string? countPrefix = null)
    {
        var arcSeg = pl.GetArcSegment2dAt(i);
        Point2d c2 = arcSeg.Center;
        double r = arcSeg.Radius;

        Point2d a = pl.GetPoint2dAt(i), b = pl.GetPoint2dAt(j);
        // 弧中点用角度中点（逆时针跨弧扫掠的中线），对半圆弧(180°，U槽端头)也正确——
        // 旧法用 toA+toB 对径点会归一出 0 除零。
        double an0 = System.Math.Atan2(a.Y - c2.Y, a.X - c2.X);
        double an1 = System.Math.Atan2(b.Y - c2.Y, b.X - c2.X);
        double sweep = an1 - an0;
        if (sweep <= 0) sweep += 2.0 * System.Math.PI;
        double amid = an0 + sweep * 0.5;
        Point2d arcMid = c2 + new Vector2d(System.Math.Cos(amid), System.Math.Sin(amid)) * r;
        var center = new Point3d(c2.X, c2.Y, 0);
        var chord = new Point3d(arcMid.X, arcMid.Y, 0);

        // 引线长度：外延一段，文字坐在末端(GB 圆角标注：圆心->弧中点->外延引线->R文字)。
        double leader = System.Math.Max(baseGap * 0.6, r * 0.4);
        // 圆弧半径按名义值显示(四舍五入到 0.1，去尾零)：R6.008 标成 R6 而非 R6.01；
        // 同半径圆角(≥3 个)代表弧的文字带数量前缀 "4×R1.5"，文字与引线一体
        string txt = (countPrefix ?? "") + "R" + FormatLen(System.Math.Round(r, 1));
        var dim = new RadialDimension(center, chord, leader, txt, dimStyleId);
        DimUtil.Append(db, tr, space, dim, dimStyleId, layerId);

        // 给圆弧补圆心十字线(GB/T 4458.1：圆角/圆弧应有圆心点画线)。
        // 过hang 仍取 radius+3mm，跟 CircleDimensioner 里孔的中心线保持一致。
        if (ext.HasValue)
            CenterlineHelper.AddCross(db, tr, space, center, r, ext.Value);
    }

    /// <summary>独立 Arc 实体的圆角标注：与 Polyline 弧段同法（圆心→弧中点→外延引线→R文字）。</summary>
    private static void AnnotateStandaloneArc(Database db, Transaction tr, BlockTableRecord space,
        Point2d a, Point2d b, Point2d c2, double r, ObjectId dimStyleId, ObjectId layerId,
        double baseGap, Extents3d? ext = null, string? countPrefix = null)
    {
        double an0 = System.Math.Atan2(a.Y - c2.Y, a.X - c2.X);
        double an1 = System.Math.Atan2(b.Y - c2.Y, b.X - c2.X);
        double sweep = an1 - an0;
        if (sweep <= 0) sweep += 2.0 * System.Math.PI;
        double amid = an0 + sweep * 0.5;
        Point2d arcMid = c2 + new Vector2d(System.Math.Cos(amid), System.Math.Sin(amid)) * r;

        var center = new Point3d(c2.X, c2.Y, 0);
        var chord = new Point3d(arcMid.X, arcMid.Y, 0);
        double leader = System.Math.Max(baseGap * 0.6, r * 0.4);
        string txt = (countPrefix ?? "") + "R" + FormatLen(System.Math.Round(r, 1));
        var dim = new RadialDimension(center, chord, leader, txt, dimStyleId);
        DimUtil.Append(db, tr, space, dim, dimStyleId, layerId);
        if (ext.HasValue)
            CenterlineHelper.AddCross(db, tr, space, center, r, ext.Value);
    }

    // ---------- 工具 ----------

    /// <summary>闭合多段线质心（面积加权）；退化则取顶点平均。</summary>
    private static Point2d PolylineCentroid(Polyline pl)
    {
        double cx = 0, cy = 0, area = 0;
        int n = pl.NumberOfVertices;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            Point2d pi = pl.GetPoint2dAt(i), pj = pl.GetPoint2dAt(j);
            double cross = pi.X * pj.Y - pj.X * pi.Y;
            area += cross;
            cx += (pi.X + pj.X) * cross;
            cy += (pi.Y + pj.Y) * cross;
        }
        area *= 0.5;
        if (System.Math.Abs(area) < 1e-9)
        {
            double ax = 0, ay = 0;
            for (int i = 0; i < n; i++) { ax += pl.GetPoint2dAt(i).X; ay += pl.GetPoint2dAt(i).Y; }
            return new Point2d(ax / n, ay / n);
        }
        return new Point2d(cx / (6 * area), cy / (6 * area));
    }

    private static string FormatLen(double v)
    {
        double r = System.Math.Round(v, 2);
        if (System.Math.Abs(r - System.Math.Round(r)) < 1e-9)
            return ((long)System.Math.Round(r)).ToString();
        return r.ToString("0.##");
    }
}
