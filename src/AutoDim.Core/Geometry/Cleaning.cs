namespace AutoDim.Core.Geometry;

/// <summary>
/// 几何清洗：端点吸附 -> 完全去重 -> 共线微段合并（仅直线段；圆弧弦不合并）。
/// 输入可以是任意碎线段（LINE / 多段线边 / 圆弧单段弦），输出干净的合并线段。
/// </summary>
public static class Cleaning
{
    public static List<Seg> CleanSegments(
        IEnumerable<Seg> segments,
        double snapTol = 0.5,
        double mergeTol = 1.0,
        double angleBucketDeg = 0.5)
    {
        // 1) 吸附 + 完全去重（同端点对方向无关；同一对同时有直线/弧时保留弧）
        //    圆弧端点在网格化后会用"原始圆心"重算 bulge：SW 导出圆弧的端点被 0.5 网格
        //    取整走样会让 bulge 重建的圆心漂移(R6 被标成 R5.69 的根因)；保持圆心不变、
        //    半径随端点微调，既保住标注精度又不改变直线碎片网络。
        var seen = new Dictionary<(Point2D, Point2D), Seg>();
        foreach (var s0 in segments)
        {
            var a = Point2D.Snap(s0.A, snapTol);
            var b = Point2D.Snap(s0.B, snapTol);
            if (a == b) continue;
            double bl = s0.Bulge;
            if (bl != 0)
                bl = KeepArcCenter(s0.A, s0.B, bl, a, b);
            bool flip = b.X < a.X || (b.X == a.X && b.Y < a.Y);
            var key = flip ? (b, a) : (a, b);
            bl = flip ? -bl : bl;
            if (seen.TryGetValue(key, out var existing))
            {
                if (existing.Bulge == 0 && bl != 0)
                    seen[key] = new Seg(a, b, bl);
                continue;
            }
            seen[key] = new Seg(a, b, bl);
        }

        // 2) 只合并直线段，圆弧弦原样保留
        var arcs = new List<Seg>();
        var straight = new List<Seg>();
        foreach (var s in seen.Values)
        {
            if (s.Bulge != 0) arcs.Add(s);
            else straight.Add(s);
        }

        var groups = new Dictionary<(int Ang, int Off), List<SegInfo>>();
        foreach (var s in straight)
        {
            double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) continue;

            double angDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (angDeg < 0) angDeg += 180.0;
            int angBucket = (int)Math.Round(angDeg / angleBucketDeg);

            double ux = dx / len, uy = dy / len;
            if (ux < 0 || (Math.Abs(ux) < 1e-12 && uy < 0)) { ux = -ux; uy = -uy; }
            double nx = -uy, ny = ux;
            double off = ((s.A.X + s.B.X) / 2) * nx + ((s.A.Y + s.B.Y) / 2) * ny;
            // 分组桶取 mergeTol 的一半：相距 ≤mergeTol/2 的平行线才视为同一条线。
            // 原实现用整档 mergeTol，会把相距 0.5mm 的重复轮廓(扫描图常见)误并成
            // "平均线"，端点 Snap 错位后整个面丢失(dup05 实测 faces=0)。
            int offBucket = (int)Math.Round(off / (mergeTol * 0.5));

            var key = (angBucket, offBucket);
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = new List<SegInfo>();
            list.Add(new SegInfo(s.A, s.B, ux, uy, nx, ny, off));
        }

        var merged = new List<Seg>();
        foreach (var grp in groups.Values)
        {
            double ux = grp[0].Ux, uy = grp[0].Uy, nx = grp[0].Nx, ny = grp[0].Ny;
            double offMid = grp.Average(g => g.Off);

            var intervals = new List<(double S, double E)>();
            foreach (var g in grp)
            {
                double ta = g.A.X * ux + g.A.Y * uy;
                double tb = g.B.X * ux + g.B.Y * uy;
                intervals.Add((Math.Min(ta, tb), Math.Max(ta, tb)));
            }
            intervals.Sort((x, y) => x.S.CompareTo(y.S));

            var union = new List<(double S, double E)>();
            double cs = intervals[0].S, ce = intervals[0].E;
            for (int i = 1; i < intervals.Count; i++)
            {
                if (intervals[i].S <= ce + mergeTol)
                    ce = Math.Max(ce, intervals[i].E);
                else
                {
                    union.Add((cs, ce));
                    cs = intervals[i].S;
                    ce = intervals[i].E;
                }
            }
            union.Add((cs, ce));

            foreach (var (s, e) in union)
            {
                if (e - s < mergeTol) continue;
                var p1 = Point2D.Snap(new Point2D(ux * s + nx * offMid, uy * s + ny * offMid), snapTol);
                var p2 = Point2D.Snap(new Point2D(ux * e + nx * offMid, uy * e + ny * offMid), snapTol);
                if (p1 != p2)
                    merged.Add(new Seg(p1, p2));
            }
        }

        merged.AddRange(arcs);
        return merged;
    }

    /// <summary>网格化移动了弧端点，重算 bulge 使圆心保持在原始位置：
    /// 已知原始弦+bulge 反推圆心，再对新弦求"圆心距不变"的 bulge。</summary>
    private static double KeepArcCenter(Point2D a0, Point2D b0, double b0b, Point2D a1, Point2D b1)
    {
        if (Math.Abs(b0b) < 1e-9) return b0b;
        // 原始圆心：h = 半弦长 × (1-b²)/(2b)，沿 A→B 左法线(bulge>0 在左)
        double dx0 = b0.X - a0.X, dy0 = b0.Y - a0.Y;
        double L0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0) / 2;
        if (L0 < 1e-9) return b0b;
        double h0 = L0 * (1.0 - b0b * b0b) / (2.0 * b0b);
        double nx0 = -dy0 / (2 * L0), ny0 = dx0 / (2 * L0);
        double cx = (a0.X + b0.X) / 2 + nx0 * h0;
        double cy = (a0.Y + b0.Y) / 2 + ny0 * h0;
        // 新弦的圆心有符号距离
        double dx1 = b1.X - a1.X, dy1 = b1.Y - a1.Y;
        double L1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1) / 2;
        if (L1 < 1e-9) return b0b;
        double nx1 = -dy1 / (2 * L1), ny1 = dx1 / (2 * L1);
        double h1 = (cx - (a1.X + b1.X) / 2) * nx1 + (cy - (a1.Y + b1.Y) / 2) * ny1;
        if (Math.Abs(h1) < 1e-9) return 0.0;
        // b = sign(h1) × (sqrt(h1²+L1²) - |h1|) / L1
        double mag = Math.Sqrt(h1 * h1 + L1 * L1);
        return (h1 > 0 ? 1.0 : -1.0) * (mag - Math.Abs(h1)) / L1;
    }

    private readonly record struct SegInfo(
        Point2D A, Point2D B, double Ux, double Uy, double Nx, double Ny, double Off);
}
