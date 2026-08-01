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

        // —— 直径标注（GB 水平引出线注法，文字与标注一体）——
        // 流程：直径容差分桶 -> 桶内按空间位置聚类分组 -> 每组选一个代表孔标 "N×Ød"。
        //   * 容差分桶：SW 导出的半径带浮点误差(如 0.8249999999999886 vs 0.825000000000017)，
        //     旧的 r*20 取整会在 0.05 边界把同一规格孔劈成两个桶(1.65 变成 6+2)，必须容差合并。
        //   * 空间分组：同直径但位置不同的孔(远处孔组/独立孔)必须分开标注——
        //     test3 的 1.55 孔是"下方 6 个 + 上方远处 2 个"，不能合并成 8×Ø1.55。
        //   * 每组标一个代表孔，文字 "N×Ød"(N=组内孔数)；组内其余孔不再重复标(阵列注法)。
        double cx = 0, cy = 0;
        foreach (var c in circles) { cx += c.Center.X; cy += c.Center.Y; }
        cx /= circles.Count;
        cy /= circles.Count;
        var centroid = new Point3d(cx, cy, 0);
        double midX = ext.HasValue ? (ext.Value.MinPoint.X + ext.Value.MaxPoint.X) * 0.5 : centroid.X;
        var buckets = new List<List<Circle>>();
        foreach (var c in circles)
        {
            var bucket = buckets.FirstOrDefault(b => System.Math.Abs(b[0].Radius - c.Radius) < 0.005);
            if (bucket == null)
            {
                bucket = new List<Circle>();
                buckets.Add(bucket);
            }
            bucket.Add(c);
        }

        foreach (var bucket in buckets)
        {
            var groups = ClusterByPosition(bucket);
            // 镜像对称合并：左右对称的两组(孔数相同、关于竖直轴对称)合并为 1 组标注，
            // N=两组合计(如左右各 2×2 -> 8×Ød；上方对称单孔对 -> 2×Ød)。国标对称件注法。
            var merged = new bool[groups.Count];
            for (int i = 0; i < groups.Count; i++)
            {
                if (merged[i]) continue;
                for (int j = i + 1; j < groups.Count; j++)
                {
                    if (merged[j]) continue;
                    if (IsMirrorPair(groups[i], groups[j], 0.05))
                    {
                        groups[i].AddRange(groups[j]);
                        merged[j] = true;
                    }
                }
            }
            for (int g = 0; g < groups.Count; g++)
            {
                if (merged[g]) continue;
                var group = groups[g];
                // 代表孔：组内最左下(引出线向右上展开；孔在中轴线右侧时 AddDiameterLeader 自动向左)
                var rep = group.OrderBy(p => p.Center.X).ThenBy(p => p.Center.Y).First();
                string txt = group.Count > 1
                    ? $"{group.Count}×Ø{FormatLen(rep.Radius * 2.0)}"
                    : "Ø" + FormatLen(rep.Radius * 2.0);
                AddDiameterLeader(db, tr, space, rep.Center, rep.Radius, txt, dimStyleId, layerId, midX);
                dia++;
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

    /// <summary>桶内按空间位置聚类分组：先对全部孔对建最小生成树(MST)，取边长中位数
    /// 的 2.5 倍作切断阈值——远处孔组/独立孔被切开(如 test3 上方 2 个 1.55 与下方 6 个
    /// 分开)，同一位置的规则阵列/镜像组保留同组。组内孔数即 N×Ød 的 N。</summary>
    private static List<List<Circle>> ClusterByPosition(List<Circle> group)
    {
        int n = group.Count;
        if (n <= 1) return new List<List<Circle>> { group };
        var edges = new List<(int A, int B, double D)>();
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double dx = group[i].Center.X - group[j].Center.X;
                double dy = group[i].Center.Y - group[j].Center.Y;
                edges.Add((i, j, System.Math.Sqrt(dx * dx + dy * dy)));
            }
        edges.Sort((x, y) => x.D.CompareTo(y.D));

        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        var mst = new List<(int A, int B, double D)>();
        foreach (var e in edges)
        {
            int ra = Find(e.A), rb = Find(e.B);
            if (ra != rb) { parent[ra] = rb; mst.Add(e); }
        }
        var ds = mst.Select(e => e.D).OrderBy(v => v).ToList();
        // 切断阈值：MST 边长排序后找最大相邻间隙比(如 16.5 -> 39.6 比值 2.4)，
        // 比值 > 2.0 视为"组间远距"切断；均匀阵列内部没有这种跳变，不会误切。
        double cut = double.MaxValue;
        double bestRatio = 1.0;
        for (int i = 0; i < ds.Count - 1; i++)
        {
            if (ds[i] < 1e-9) continue;
            double r = ds[i + 1] / ds[i];
            if (r > bestRatio) { bestRatio = r; cut = ds[i + 1] - 1e-9; }
        }
        if (bestRatio <= 2.0) cut = double.MaxValue;   // 无明显组间间隙 -> 整桶一组
        for (int i = 0; i < n; i++) parent[i] = i;
        foreach (var e in mst)
            if (e.D <= cut)
            {
                int ra = Find(e.A), rb = Find(e.B);
                if (ra != rb) parent[ra] = rb;
            }
        var map = new Dictionary<int, List<Circle>>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!map.TryGetValue(r, out var list)) map[r] = list = new List<Circle>();
            list.Add(group[i]);
        }
        return map.Values.ToList();
    }

    /// <summary>两组孔是否关于竖直轴左右镜像(孔数相同、每点都能在另一组找到对称对应点)。
    /// 用于把"左右对称的两组阵列/单孔"合并为一个 N×Ød 标注。</summary>
    private static bool IsMirrorPair(List<Circle> a, List<Circle> b, double tol)
    {
        if (a.Count != b.Count) return false;
        double minX = double.MaxValue, maxX = double.MinValue;
        foreach (var c in a)
        {
            minX = System.Math.Min(minX, c.Center.X);
            maxX = System.Math.Max(maxX, c.Center.X);
        }
        foreach (var c in b)
        {
            minX = System.Math.Min(minX, c.Center.X);
            maxX = System.Math.Max(maxX, c.Center.X);
        }
        double sym = (minX + maxX) * 0.5;
        var used = new bool[b.Count];
        foreach (var ca in a)
        {
            double mx = 2 * sym - ca.Center.X;
            bool found = false;
            for (int k = 0; k < b.Count; k++)
            {
                if (used[k]) continue;
                if (System.Math.Abs(b[k].Center.X - mx) < tol &&
                    System.Math.Abs(b[k].Center.Y - ca.Center.Y) < tol)
                {
                    used[k] = true;
                    found = true;
                    break;
                }
            }
            if (!found) return false;
        }
        return true;
    }

    /// <summary>国标"折线引出线"直径标注：实心箭头从圆周引出，先斜线段(约 45°)再折成
    /// 水平直线，文字 ⌀d 写在水平线段上方居中。用 Line+Solid+MText 组合——小孔的文字
    /// 比圆大，"过圆心两端箭头+中间文字"放不下，折线引出线是 GB 标准注法。
    /// 孔在中轴线右侧时向左引出，避免文字飞出图外。
    /// 箭头/斜线随孔尺寸自适应：小孔配小箭头、短斜线(旧版固定按 baseGap 甩长线+默认
    /// 2.5mm 箭头，0.8mm 半径的孔上画 1.25mm 箭头+6.7mm 斜线，严重不成比例)。</summary>
    private static void AddDiameterLeader(Database db, Transaction tr, BlockTableRecord space,
        Point3d center, double radius, string txt, ObjectId dimStyleId, ObjectId layerId, double midX)
    {
        double defAsz = 2.5, txtH = 2.5;
        ObjectId textStyleId = ObjectId.Null;
        try
        {
            var dst = (DimStyleTableRecord)tr.GetObject(dimStyleId, OpenMode.ForRead);
            defAsz = dst.Dimasz * dst.Dimscale;
            txtH = dst.Dimtxt * dst.Dimscale;
            textStyleId = dst.Dimtxsty;   // ADIM 样式的国标文字样式
        }
        catch { }
        int dir = center.X >= midX ? -1 : 1;   // 孔在图中轴线右侧 -> 向左引出，否则向右
        // 折线：圆周点(箭头) -> 斜线45° -> 水平线(文字区)
        // 箭头：随孔缩小(约 0.9×半径，下限 0.6 保证可见)，绝不大于默认标注箭头
        double asz = System.Math.Clamp(radius * 0.9, 0.6, defAsz);
        // 斜线：与箭头成比例(45°斜线两直角边 ≈ 2.2×箭头)，小孔不再甩长线
        double slope = System.Math.Clamp(asz * 2.2, 1.5, System.Math.Max(1.5, radius * 2.0));
        var pCirc = new Point3d(center.X + dir * radius, center.Y, 0);              // 箭头尖端(圆周)
        var pBend = new Point3d(pCirc.X + dir * slope, pCirc.Y + slope, 0);         // 折点(斜线转水平)

        // 文字：先以临时位置创建并读实际渲染宽度，水平线长度按"文字宽+两端余量"自适应，
        // 不再用 0.7×高×字符数的粗略估算(实际 SHX 字形宽度与估算偏差大，线过长或过短)
        using var mt = new MText();
        mt.SetDatabaseDefaults(db);
        if (!textStyleId.IsNull)
            mt.TextStyleId = textStyleId;
        mt.Contents = txt.Replace("Ø", "%%c");
        mt.TextHeight = txtH;
        mt.Attachment = AttachmentPoint.MiddleCenter;
        mt.LayerId = layerId;
        mt.Location = center;   // 临时位置，仅用于读渲染宽度
        double textW = System.Math.Max(8.0, 0.7 * txtH * txt.Length);              // 回退估算
        try
        {
            var ge = mt.GeometricExtents;
            double w = ge.MaxPoint.X - ge.MinPoint.X;
            if (w > 0.5) textW = w + System.Math.Max(1.0, txtH * 0.8);              // 两端各留 ~1mm
        }
        catch { }
        var pEnd = new Point3d(pBend.X + dir * textW, pBend.Y, 0);                  // 水平线末端

        // 实心箭头：尖端在圆周，沿斜线方向指向圆心
        double aLen = pBend.X - pCirc.X, aH = pBend.Y - pCirc.Y;
        double aN = System.Math.Sqrt(aLen * aLen + aH * aH);
        double ux = aLen / aN, uy = aH / aN;   // 斜线方向(从圆向外)
        using var solid = new Solid();
        solid.SetDatabaseDefaults(db);
        solid.SetPointAt(0, new Point3d(pCirc.X - ux * asz - (-uy) * asz * 0.5,
                                        pCirc.Y - uy * asz - ux * asz * 0.5, 0));
        solid.SetPointAt(1, new Point3d(pCirc.X - ux * asz + (-uy) * asz * 0.5,
                                        pCirc.Y - uy * asz + ux * asz * 0.5, 0));
        solid.SetPointAt(2, new Point3d(pCirc.X, pCirc.Y, 0));
        solid.SetPointAt(3, new Point3d(pCirc.X, pCirc.Y, 0));
        solid.LayerId = layerId;
        space.AppendEntity(solid);
        tr.AddNewlyCreatedDBObject(solid, true);
        Cad.AdimMarker.Mark(db, tr, solid);

        // 斜线段 + 水平段
        using var ln1 = new Line(pCirc, pBend);
        ln1.SetDatabaseDefaults(db);
        ln1.LayerId = layerId;
        space.AppendEntity(ln1);
        tr.AddNewlyCreatedDBObject(ln1, true);
        Cad.AdimMarker.Mark(db, tr, ln1);
        using var ln = new Line(pBend, pEnd);
        ln.SetDatabaseDefaults(db);
        ln.LayerId = layerId;
        space.AppendEntity(ln);
        tr.AddNewlyCreatedDBObject(ln, true);
        Cad.AdimMarker.Mark(db, tr, ln);

        // 文字：水平线段上方居中、水平(位置在上一步定好的水平线上方)
        mt.Location = new Point3d(pBend.X + dir * textW * 0.5, pBend.Y + txtH * 0.6, 0);
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
