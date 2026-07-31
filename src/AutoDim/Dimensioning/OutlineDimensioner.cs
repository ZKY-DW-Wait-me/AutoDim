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
        // 短碎边不标：相对该轮廓尺度自适应（0.35×偏移量），过滤矢量化噪声的亚毫米/毫米级碎段
        double minSegLen = System.Math.Max(0.5, baseGap * 0.35);

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
                cands.Add((i, len));
            }
            cands.Sort((x, y) => y.Len.CompareTo(x.Len));
            if (cands.Count > MaxSegDims)
                cands = cands.GetRange(0, MaxSegDims);
            foreach (var (i, _) in cands)
                seg += AnnotateLine(db, tr, space, pl, i, (i + 1) % n, centroid, dimStyleId, layerId, baseGap);

            // 圆弧段：按半径从大到小取前 MaxArcDims 条（R 引线是重叠大户，小圆角不逐个标）
            var arcCands = new List<(int I, double R)>();
            for (int i = 0; i < n; i++)
            {
                if (!pl.Closed && i == n - 1) break;
                if (System.Math.Abs(pl.GetBulgeAt(i)) < 1e-9) continue;
                arcCands.Add((i, pl.GetArcSegment2dAt(i).Radius));
            }
            arcCands.Sort((x, y) => y.R.CompareTo(x.R));
            if (arcCands.Count > MaxArcDims)
                arcCands = arcCands.GetRange(0, MaxArcDims);
            foreach (var (i, _) in arcCands)
            {
                int j = (i + 1) % n;
                AnnotateArc(db, tr, space, pl, i, j, centroid, dimStyleId, layerId, baseGap, ext);
                arc++;
            }
        }
        return (seg, arc);
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

    private static int AnnotateLine(Database db, Transaction tr, BlockTableRecord space,
        Polyline pl, int i, int j, Point2d centroid, ObjectId dimStyleId, ObjectId layerId, double baseGap)
    {
        Point2d a = pl.GetPoint2dAt(i), b = pl.GetPoint2dAt(j);
        Vector2d d = b - a;
        double dx = System.Math.Abs(d.X), dy = System.Math.Abs(d.Y);

        // 水平或垂直边：直接标边长(强制消零)
        if (dx < Tol || dy < Tol)
        {
            AddAligned(db, tr, space, a, b, centroid, dimStyleId, layerId, baseGap, FormatLen((b - a).Length));
            return 1;
        }

        // 斜边：直角三角形，自由度=2。GB/T 4458.4：斜边已由直角坐标(dx,dy)定义时不得标角度
        // (角度冗余+四舍五入致数值矛盾)。所以斜边永远只标两个直角边(脏的带小数)，绝不标夹角。
        double dxV = d.X, dyV = d.Y;
        double dxAbs = System.Math.Abs(dxV), dyAbs = System.Math.Abs(dyV);
        AddProjectionDims(db, tr, space, a, b, centroid, dxAbs, dyAbs, dimStyleId, layerId, baseGap);
        return 2;
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
        Point2d dl2 = mid + n2 * (0.5 * baseGap);

        var dim = new AlignedDimension(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0),
                                       new Point3d(dl2.X, dl2.Y, 0), "", dimStyleId);
        DimUtil.Append(db, tr, space, dim, dimStyleId, layerId, text);
    }

    /// <summary>斜边的水平投影 + 垂直投影两个 RotatedDimension(模式 A)。
    /// 排版关键：两条尺寸线分放斜边【法线方向】外侧，但垂直投影比水平投影外推更远(2.0×baseGap vs 1.0×)，
    /// 避免两者延伸线在角点附近交叉、文字撞车。垂直尺寸文字强制贴尺寸线左侧(TextRotation=-90°)。</summary>
    private static void AddProjectionDims(Database db, Transaction tr, BlockTableRecord space,
        Point2d a, Point2d b, Point2d centroid, double dx, double dy,
        ObjectId dimStyleId, ObjectId layerId, double baseGap)
    {
        Vector2d d = b - a;
        Vector2d n2 = new Vector2d(-d.Y, d.X) / d.Length;
        Point2d mid = a + d * 0.5;
        if (n2.DotProduct(centroid - mid) > 0) n2 = -n2;   // 翻到外侧

        // 水平投影：尺寸线水平，y 放斜边 y 范围外侧，距斜边 1.0×baseGap(近)
        double yH = n2.Y > 0 ? System.Math.Max(a.Y, b.Y) + 1.0 * baseGap : System.Math.Min(a.Y, b.Y) - 1.0 * baseGap;
        var pH1 = new Point3d(a.X, a.Y, 0);
        var pH2 = new Point3d(b.X, b.Y, 0);
        var dimH = new RotatedDimension(0.0, pH1, pH2, new Point3d((a.X + b.X) * 0.5, yH, 0), "", dimStyleId);
        DimUtil.Append(db, tr, space, dimH, dimStyleId, layerId, FormatLen(dx));

        // 垂直投影：尺寸线垂直，x 放斜边 x 范围外侧，距斜边 2.0×baseGap(远，避让水平投影延伸线)
        double xV = n2.X > 0 ? System.Math.Max(a.X, b.X) + 2.0 * baseGap : System.Math.Min(a.X, b.X) - 2.0 * baseGap;
        var pV1 = new Point3d(a.X, a.Y, 0);
        var pV2 = new Point3d(b.X, b.Y, 0);
        var dimV = new RotatedDimension(System.Math.PI * 0.5, pV1, pV2, new Point3d(xV, (a.Y + b.Y) * 0.5, 0), "", dimStyleId);
        DimUtil.Append(db, tr, space, dimV, dimStyleId, layerId, FormatLen(dy));
        // 垂直尺寸文字贴尺寸线左侧、字头朝左(GB 垂直尺寸惯例)，避免文字骑在尺寸线上被穿过
        dimV.TextRotation = -System.Math.PI * 0.5;
        dimV.RecomputeDimensionBlock(true);
    }

    // ---------- 圆弧段（引线从圆心穿过弧中点向外，朝向远离邻边交点） ----------

    private static void AnnotateArc(Database db, Transaction tr, BlockTableRecord space,
        Polyline pl, int i, int j, Point2d centroid, ObjectId dimStyleId, ObjectId layerId, double baseGap,
        Extents3d? ext = null)
    {
        var arcSeg = pl.GetArcSegment2dAt(i);
        Point2d c2 = arcSeg.Center;
        double r = arcSeg.Radius;

        // 弧中点方向 = 两端点(相对圆心)方向的和向量 toA+toB。
        // 几何事实：对 <180° 的凸弧(圆角必然如此)，toA+toB 指向"两端点之间"即弧中点方向(有弧的那侧)。
        // 不要用质心翻向 —— 那会把引线翻到没有弧的虚空(180°-270° 弧时质心若在同侧就会错)。
        Point2d a = pl.GetPoint2dAt(i), b = pl.GetPoint2dAt(j);
        Vector2d toA = a - c2; toA = toA / toA.Length;
        Vector2d toB = b - c2; toB = toB / toB.Length;
        Vector2d bis = toA + toB; bis = bis / bis.Length;

        Point2d arcMid = c2 + bis * r;     // 弧中点(圆周上，有弧的那侧)
        var center = new Point3d(c2.X, c2.Y, 0);
        var chord = new Point3d(arcMid.X, arcMid.Y, 0);

        // 引线长度：外延一段，文字坐在末端(GB 圆角标注：圆心->弧中点->外延引线->R文字)。
        double leader = System.Math.Max(baseGap * 0.6, r * 0.4);
        string txt = "R" + FormatLen(r);
        var dim = new RadialDimension(center, chord, leader, txt, dimStyleId);
        DimUtil.Append(db, tr, space, dim, dimStyleId, layerId);

        // 给圆弧补圆心十字线(GB/T 4458.1：圆角/圆弧应有圆心点画线)。
        // 过hang 仍取 radius+3mm，跟 CircleDimensioner 里孔的中心线保持一致。
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
