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
        var seen = new Dictionary<(Point2D, Point2D), Seg>();
        foreach (var s0 in segments)
        {
            var a = Point2D.Snap(s0.A, snapTol);
            var b = Point2D.Snap(s0.B, snapTol);
            if (a == b) continue;
            bool flip = b.X < a.X || (b.X == a.X && b.Y < a.Y);
            var key = flip ? (b, a) : (a, b);
            double bl = flip ? -s0.Bulge : s0.Bulge;
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
            int offBucket = (int)Math.Round(off / mergeTol);

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

    private readonly record struct SegInfo(
        Point2D A, Point2D B, double Ux, double Uy, double Nx, double Ny, double Off);
}
