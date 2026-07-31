namespace AutoDim.Core.Geometry;

/// <summary>
/// 几何清洗：端点吸附 -> 完全去重 -> 共线微段合并。
/// 输入可以是任意碎线段（LINE / 多段线边 / 圆弧采样弦），输出干净的合并线段。
/// </summary>
public static class Cleaning
{
    public static List<(Point2D A, Point2D B)> CleanSegments(
        IEnumerable<(Point2D A, Point2D B)> segments,
        double snapTol = 0.5,
        double mergeTol = 1.0,
        double angleBucketDeg = 0.5)
    {
        // 1) 吸附 + 完全去重（同端点对，方向无关）
        var seen = new HashSet<(Point2D, Point2D)>();
        var segs = new List<(Point2D A, Point2D B)>();
        foreach (var (a0, b0) in segments)
        {
            var a = Point2D.Snap(a0, snapTol);
            var b = Point2D.Snap(b0, snapTol);
            if (a == b) continue;
            var key = a.X < b.X || (a.X == b.X && a.Y <= b.Y) ? (a, b) : (b, a);
            if (!seen.Add(key)) continue;
            segs.Add((a, b));
        }

        // 2) 共线合并：按 (角度桶, 法向偏移桶) 分组，桶内投影区间并集
        var groups = new Dictionary<(int Ang, int Off), List<SegInfo>>();
        foreach (var (a, b) in segs)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) continue;

            double angDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (angDeg < 0) angDeg += 180.0;
            int angBucket = (int)Math.Round(angDeg / angleBucketDeg);

            double ux = dx / len, uy = dy / len;
            if (ux < 0 || (Math.Abs(ux) < 1e-12 && uy < 0)) { ux = -ux; uy = -uy; }
            double nx = -uy, ny = ux;
            double off = ((a.X + b.X) / 2) * nx + ((a.Y + b.Y) / 2) * ny;
            int offBucket = (int)Math.Round(off / mergeTol);

            var key = (angBucket, offBucket);
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = new List<SegInfo>();
            list.Add(new SegInfo(a, b, ux, uy, nx, ny, off));
        }

        var merged = new List<(Point2D A, Point2D B)>();
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
                    merged.Add((p1, p2));
            }
        }

        return merged;
    }

    private readonly record struct SegInfo(
        Point2D A, Point2D B, double Ux, double Uy, double Nx, double Ny, double Off);
}
