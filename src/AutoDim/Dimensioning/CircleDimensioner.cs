using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoDim.Dimensioning;

/// <summary>
/// ③ 孔/圆：每个圆一个直径标注——用 RadialDimension 从圆心引出径向线、箭头指到圆周、
/// 文字覆盖为 ⌀直径值（GB 孔标准注法）。孔心定位用从基准边出发的线性尺寸链，放内层 tier；
/// 经过孔心的延伸线从孔心起（GB：尺寸界线从被测要素出发），基准边的延伸线从零件边缘起。
/// </summary>
internal static class CircleDimensioner
{
    private const int MaxChainPts = 11;  // 定位链最多段数；更密则跳过（链变噪音且压线）
    private const double MinChainGap = 8.0;   // 链相邻点最小间距；更近则文字必互相压，跳过该链

    /// <returns>(直径标注数, 定位标注数)</returns>
    public static (int diameters, int positions) Annotate(
        Database db, Transaction tr, BlockTableRecord space,
        IReadOnlyList<ObjectId> ids, Extents3d? ext, ObjectId dimStyleId, ObjectId layerId, double baseGap)
    {
        var circles = new List<Circle>();
        foreach (var id in ids)
            if (tr.GetObject(id, OpenMode.ForRead) is Circle c)
                circles.Add(c);
        if (circles.Count == 0) return (0, 0);

        int dia = 0, pos = 0;

        // —— 直径标注：从圆心引径向线，箭头指到圆周，文字覆盖为 ⌀直径。
        // 方向从孔簇质心向外辐射：密集簇中线条散开，减少径向线/文字互压 ——
        double cx = 0, cy = 0;
        foreach (var c in circles) { cx += c.Center.X; cy += c.Center.Y; }
        cx /= circles.Count;
        cy /= circles.Count;
        var centroid = new Point3d(cx, cy, 0);
        foreach (var c in circles)
        {
            var to = c.Center - centroid;
            Vector3d dir = to.Length > 1e-9
                ? to.GetNormal()
                : new Vector3d(0.70710678118, 0.70710678118, 0.0);
            Point3d chordPt = c.Center + dir * c.Radius;   // 圆周上的点（箭头位置）
            string txt = "%%c" + FormatLen(c.Radius * 2.0); // %%c = ⌀
            DimUtil.Append(db, tr, space,
                new RadialDimension(c.Center, chordPt, c.Radius * 0.8, txt, dimStyleId), dimStyleId, layerId);
            dia++;
        }

        if (ext == null) return (dia, pos);
        Extents3d e = ext.Value;

        // —— 给每个圆补画十字中心线(GB：孔/圆必须有中心线，点划线引到零件外便于定位)。
        // 放这里：即使定位链因过密被跳过，中心线也照画 ——
        foreach (var c in circles)
            CenterlineHelper.AddCross(db, tr, space, c.Center, c.Radius, e);

        // 定位尺寸链放中圈 tier(2.0×baseGap)：在轮廓分段(0.5×)之外、总体(3.5×)之内。
        double posOff = 2.0 * baseGap;
        double tol = baseGap * 0.05 + 1e-6;

        // —— X 定位链：尺寸链 minX→cx1→cx2→...→maxX。
        // 延伸线起点用【圆心】(GB：尺寸界线从被测要素出发)，从圆心垂直引到零件下方的尺寸线。
        // 基准端(左下角)与末端(右下角)用零件角点。延伸线穿零件实体是内部孔定位的 GB 常态。
        var holeXs = circles.Select(c => c.Center.X).Distinct().OrderBy(v => v).ToList();
        double bottomY = e.MinPoint.Y, topY = e.MaxPoint.Y, rightX = e.MaxPoint.X;
        bool xChainOk = holeXs.Count + 1 <= MaxChainPts && MinGapOk(holeXs);
        if (xChainOk)
        {
            // 每个 X 对应的圆心 Y(同一 X 若有多孔，取第一个；延伸线都从该圆心引)
            var xToY = new Dictionary<double, double>();
            foreach (var c in circles)
                if (!xToY.ContainsKey(c.Center.X)) xToY[c.Center.X] = c.Center.Y;

            var xPts = new List<double> { e.MinPoint.X };
            xPts.AddRange(holeXs);
            xPts.Add(rightX);
            double xDimY = bottomY - posOff;
            for (int i = 0; i < xPts.Count - 1; i++)
            {
                double a = xPts[i], b = xPts[i + 1];
                if (b - a <= tol) continue;
                // 首段基准=左下角(minX,bottomY)；末段终点=右下角(maxX,bottomY)；中间端点=圆心
                Point3d p1 = i == 0 ? new Point3d(a, bottomY, 0) : new Point3d(a, xToY[a], 0);
                Point3d p2 = i == xPts.Count - 2 ? new Point3d(b, bottomY, 0) : new Point3d(b, xToY[b], 0);
                var dl = new Point3d((a + b) * 0.5, xDimY, 0);
                var dim = new RotatedDimension(0.0, p1, p2, dl, "", dimStyleId);
                DimUtil.Append(db, tr, space, dim, dimStyleId, layerId, FormatLen(b - a));
                pos++;
            }
        }

        // —— Y 定位链：minY→cy1→cy2→...→maxY。延伸线从圆心水平引到零件左侧的尺寸线。
        var holeYs = circles.Select(c => c.Center.Y).Distinct().OrderBy(v => v).ToList();
        double leftX = e.MinPoint.X;
        bool yChainOk = holeYs.Count + 1 <= MaxChainPts && MinGapOk(holeYs);
        if (yChainOk)
        {
            var yToX = new Dictionary<double, double>();
            foreach (var c in circles)
                if (!yToX.ContainsKey(c.Center.Y)) yToX[c.Center.Y] = c.Center.X;

            var yPts = new List<double> { e.MinPoint.Y };
            yPts.AddRange(holeYs);
            yPts.Add(topY);
            double yDimX = leftX - posOff;
            for (int i = 0; i < yPts.Count - 1; i++)
            {
                double a = yPts[i], b = yPts[i + 1];
                if (b - a <= tol) continue;
                Point3d p1 = i == 0 ? new Point3d(leftX, a, 0) : new Point3d(yToX[a], a, 0);
                Point3d p2 = i == yPts.Count - 2 ? new Point3d(leftX, b, 0) : new Point3d(yToX[b], b, 0);
                var dl = new Point3d(yDimX, (a + b) * 0.5, 0);
                var dim = new RotatedDimension(System.Math.PI * 0.5, p1, p2, dl, "", dimStyleId);
                DimUtil.Append(db, tr, space, dim, dimStyleId, layerId, FormatLen(b - a));
                pos++;
            }
        }

        return (dia, pos);
    }

    /// <summary>相邻位置点间距是否全部 &gt;= MinChainGap（否则链文字互相压）。</summary>
    private static bool MinGapOk(List<double> sorted)
    {
        for (int i = 1; i < sorted.Count; i++)
            if (sorted[i] - sorted[i - 1] < MinChainGap)
                return false;
        return true;
    }

    /// <summary>长度格式化：整数不带小数，否则最多两位并去尾零。</summary>
    private static string FormatLen(double v)
    {
        double r = System.Math.Round(v, 2);
        if (System.Math.Abs(r - System.Math.Round(r)) < 1e-9)
            return ((long)System.Math.Round(r)).ToString();
        return r.ToString("0.##");
    }
}
