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
    private const double MinChainSeg = 3.0;   // 链段最小跨度：文字宽约 3.5mm，跨度更小放不下
                                              // 文字会被外置到界线外，与相邻链段文字互碰

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

        // —— 直径标注：重复直径合并为数量注记（GB 惯例 "N×Ød"）——
        // 同一直径(0.1mm 精度分桶)：桶内只有 1 个孔 -> 正常引线标 Ø；桶内 ≥2 个 ->
        // 只出 MText 注记 "N×Ød"（test.dwg 实测 442 个直径标注只有 6 种直径，
        // 128×Ø2.75 这类重复标注是 Radial+Rotated 重叠的最大来源；且多桶代表孔的
        // 引线方向平行、文字互相叠压，故多桶不再出代表孔引线）。
        double cx = 0, cy = 0;
        foreach (var c in circles) { cx += c.Center.X; cy += c.Center.Y; }
        cx /= circles.Count;
        cy /= circles.Count;
        var centroid = new Point3d(cx, cy, 0);
        var buckets = new Dictionary<int, List<Circle>>();
        foreach (var c in circles)
        {
            int key = (int)System.Math.Round(c.Radius * 20.0);   // 直径 0.1mm 精度
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = new List<Circle>();
            list.Add(c);
        }

        // 单例桶代表孔：自然方向角按最小夹角(90°)展开成扇形。
        // 同一组多个不同直径孔(各 1 个)若都朝孔簇质心外侧引线，角度几乎平行，
        // 文字会叠在一起(实测 Radial+Radial 撞车主力)，先在这里错开。
        const double MinRepSep = 90.0 * System.Math.PI / 180.0;
        var singles = new List<(Circle C, double A)>();
        foreach (var kv in buckets)
        {
            if (kv.Value.Count != 1) continue;
            var c = kv.Value[0];
            var dv = c.Center - centroid;
            double a = dv.Length > 1e-9
                ? System.Math.Atan2(dv.Y, dv.X)
                : System.Math.PI * 0.25;
            singles.Add((c, a));
        }
        singles.Sort((x, y) => x.A.CompareTo(y.A));
        for (int k = 1; k < singles.Count; k++)
            if (singles[k].A - singles[k - 1].A < MinRepSep)
                singles[k] = (singles[k].C, singles[k - 1].A + MinRepSep);
        var repAngles = new Dictionary<Circle, double>();
        foreach (var s in singles) repAngles[s.C] = s.A;

        double txtH = ReadDimTextHeight(db, tr, dimStyleId);
        int noteIdx = 0;
        foreach (var kv in buckets.OrderBy(kv => kv.Key))
        {
            var list = kv.Value;
            if (list.Count == 1)
            {
                var c0 = list[0];
                double ang = repAngles.TryGetValue(c0, out var a0) ? a0 : 0.0;
                var dir = new Vector3d(System.Math.Cos(ang), System.Math.Sin(ang), 0);
                Point3d chordPt = c0.Center + dir * c0.Radius;   // 圆周上的点（箭头位置）
                string txt = "%%c" + FormatLen(c0.Radius * 2.0); // %%c = ⌀
                DimUtil.Append(db, tr, space,
                    new RadialDimension(c0.Center, chordPt, c0.Radius * 0.8, txt, dimStyleId), dimStyleId, layerId);
                dia++;
            }
            else
            {
                // 多孔桶：代表孔仍标一个 Ø 引线(指明直径指向哪里)，其余孔用 N×Ød 注记
                // 表达数量——否则只有注记、孔看起来"漏标"
                var c0 = list[0];
                double ang = repAngles.TryGetValue(c0, out var a0) ? a0 : 0.0;
                var dir = new Vector3d(System.Math.Cos(ang), System.Math.Sin(ang), 0);
                Point3d chordPt = c0.Center + dir * c0.Radius;
                string txt = "%%c" + FormatLen(c0.Radius * 2.0);
                DimUtil.Append(db, tr, space,
                    new RadialDimension(c0.Center, chordPt, c0.Radius * 0.8, txt, dimStyleId), dimStyleId, layerId);
                dia++;
                if (ext == null) continue;
                double bx = 0, by = 0;
                foreach (var c in list) { bx += c.Center.X; by += c.Center.Y; }
                bx /= list.Count; by /= list.Count;
                double midY = (ext.Value.MinPoint.Y + ext.Value.MaxPoint.Y) * 0.5;
                // 同一组多个直径注记上下错开 2×字高，避免叠压
                double ny = by + (by < midY ? 1.8 : -1.8) * baseGap + noteIdx * 2.0 * txtH;
                noteIdx++;
                AddCountNote(db, tr, space, dimStyleId, layerId, new Point3d(bx, ny, 0), list.Count, list[0].Radius * 2.0);
            }
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
        // 多孔单排时只出沿排列方向的链(X 全同=Y 排时 X 链冗余；反之亦然)；
        // 单孔零件仍保留双轴定位链
        bool xChainOk = holeXs.Count + 1 <= MaxChainPts && MinGapOk(holeXs) &&
                        (holeXs.Count > 1 || circles.Count == 1);
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
            // 等距阵列合并：连续 ≥3 段等间距(如 5 个孔 4 段 20)合并为 1 段，文字 "4×20"，
            // 避免重复标一排相同的间距(GB 阵列注法)
            var xPlan = new List<(int I, int J, string? Text)>();
            int k = 0;
            while (k < xPts.Count - 1)
            {
                double d0 = xPts[k + 1] - xPts[k];
                int run = 1;
                while (k + run < xPts.Count - 1 &&
                       System.Math.Abs((xPts[k + run + 1] - xPts[k + run]) - d0) < 1e-3)
                    run++;
                if (run >= 3)
                {
                    xPlan.Add((k, k + run, $"{run}×{FormatLen(d0)}"));
                    k += run;
                }
                else
                {
                    xPlan.Add((k, k + 1, null));
                    k++;
                }
            }
            foreach (var (i, j, text) in xPlan)
            {
                double a = xPts[i], b = xPts[j];
                if (b - a <= System.Math.Max(tol, MinChainSeg)) continue;
                // 首段基准=左下角(minX,bottomY)；跨到末端的合并段终点=右下角(maxX,bottomY)；
                // 中间端点=圆心(xPts 中间点必是孔 X；末端 maxX 不在 xToY 表，查表会崩)
                Point3d p1 = i == 0 ? new Point3d(a, bottomY, 0) : new Point3d(a, xToY[a], 0);
                Point3d p2 = j >= xPts.Count - 1 ? new Point3d(b, bottomY, 0) : new Point3d(b, xToY[b], 0);
                var dl = new Point3d((a + b) * 0.5, xDimY, 0);
                var dim = new RotatedDimension(0.0, p1, p2, dl, "", dimStyleId);
                DimUtil.Append(db, tr, space, dim, dimStyleId, layerId, text ?? FormatLen(b - a));
                pos++;
            }
        }

        // —— Y 定位链：minY→cy1→cy2→...→maxY。延伸线从圆心水平引到零件左侧的尺寸线。
        var holeYs = circles.Select(c => c.Center.Y).Distinct().OrderBy(v => v).ToList();
        double leftX = e.MinPoint.X;
        bool yChainOk = holeYs.Count + 1 <= MaxChainPts && MinGapOk(holeYs) &&
                        (holeYs.Count > 1 || circles.Count == 1);
        if (yChainOk)
        {
            var yToX = new Dictionary<double, double>();
            foreach (var c in circles)
                if (!yToX.ContainsKey(c.Center.Y)) yToX[c.Center.Y] = c.Center.X;

            var yPts = new List<double> { e.MinPoint.Y };
            yPts.AddRange(holeYs);
            yPts.Add(topY);
            double yDimX = leftX - posOff;
            var yPlan = new List<(int I, int J, string? Text)>();
            int yk = 0;
            while (yk < yPts.Count - 1)
            {
                double d0 = yPts[yk + 1] - yPts[yk];
                int run = 1;
                while (yk + run < yPts.Count - 1 &&
                       System.Math.Abs((yPts[yk + run + 1] - yPts[yk + run]) - d0) < 1e-3)
                    run++;
                if (run >= 3)
                {
                    yPlan.Add((yk, yk + run, $"{run}×{FormatLen(d0)}"));
                    yk += run;
                }
                else
                {
                    yPlan.Add((yk, yk + 1, null));
                    yk++;
                }
            }
            foreach (var (i, j, text) in yPlan)
            {
                double a = yPts[i], b = yPts[j];
                if (b - a <= System.Math.Max(tol, MinChainSeg)) continue;
                Point3d p1 = i == 0 ? new Point3d(leftX, a, 0) : new Point3d(yToX[a], a, 0);
                Point3d p2 = j >= yPts.Count - 1 ? new Point3d(leftX, b, 0) : new Point3d(yToX[b], b, 0);
                var dl = new Point3d(yDimX, (a + b) * 0.5, 0);
                var dim = new RotatedDimension(System.Math.PI * 0.5, p1, p2, dl, "", dimStyleId);
                DimUtil.Append(db, tr, space, dim, dimStyleId, layerId, text ?? FormatLen(b - a));
                pos++;
            }
        }

        return (dia, pos);
    }

    /// <summary>读取 ADIM 样式实际文字高度(Dimtxt × Dimscale)。</summary>
    private static double ReadDimTextHeight(Database db, Transaction tr, ObjectId dimStyleId)
    {
        try
        {
            var dst = (DimStyleTableRecord)tr.GetObject(dimStyleId, OpenMode.ForRead);
            return dst.Dimtxt * dst.Dimscale;
        }
        catch { return 2.5; }
    }

    /// <summary>MText 数量注记 "N×Ød"，文字高度跟随 ADIM 样式，中上位置居中。</summary>
    private static void AddCountNote(Database db, Transaction tr, BlockTableRecord space,
        ObjectId dimStyleId, ObjectId layerId, Point3d at, int n, double diameter)
    {
        double txtH = ReadDimTextHeight(db, tr, dimStyleId);
        using var mt = new MText();
        mt.SetDatabaseDefaults(db);
        // 内联 Arial：×/Ø 是 Unicode 字符，图纸默认样式(txt.shx)不含这些字形会显示为问号
        mt.Contents = $"{{\\fArial|b0|i0|c0|p2;{n}×Ø{FormatLen(diameter)}}}";
        mt.TextHeight = txtH;
        mt.Location = at;
        mt.Attachment = AttachmentPoint.MiddleCenter;
        mt.LayerId = layerId;
        space.AppendEntity(mt);
        tr.AddNewlyCreatedDBObject(mt, true);
        Cad.AdimMarker.Mark(db, tr, mt);
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
