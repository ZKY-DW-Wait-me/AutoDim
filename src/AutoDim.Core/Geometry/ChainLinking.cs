namespace AutoDim.Core.Geometry;

/// <summary>
/// 开放链闭合：把开放链的悬空端点（度≤1）按"最近优先"连接（间距 &lt;= maxGap），
/// 用于把扫描图/矢量化图纸的外轮廓碎片闭合成环，再交给面片追踪出闭合特征。
/// </summary>
public static class ChainLinking
{
    public static List<Seg> LinkOpenEnds(IEnumerable<Seg> segments, double nodeTol, double maxGap)
    {
        var segs = segments.ToList();
        if (maxGap <= 0) return segs;

        var nodes = new Dictionary<Point2D, int>();
        var pts = new List<Point2D>();
        foreach (var s in segs)
        {
            var ka = Point2D.Snap(s.A, nodeTol);
            var kb = Point2D.Snap(s.B, nodeTol);
            if (ka == kb) continue;
            if (!nodes.ContainsKey(ka)) { nodes[ka] = pts.Count; pts.Add(ka); }
            if (!nodes.ContainsKey(kb)) { nodes[kb] = pts.Count; pts.Add(kb); }
        }

        var deg = new int[pts.Count];
        foreach (var s in segs)
        {
            var ka = Point2D.Snap(s.A, nodeTol);
            var kb = Point2D.Snap(s.B, nodeTol);
            if (ka == kb) continue;
            deg[nodes[ka]]++;
            deg[nodes[kb]]++;
        }

        var dangling = new List<int>();
        for (int i = 0; i < pts.Count; i++)
            if (deg[i] <= 1) dangling.Add(i);
        if (dangling.Count < 2) return segs;

        // 空间网格：cell = maxGap，距离 <= maxGap 的端点必在相邻 3x3 网格内
        double cell = Math.Max(maxGap, nodeTol);
        var grid = new Dictionary<(int, int), List<int>>();
        foreach (var n in dangling)
        {
            var key = ((int)Math.Floor(pts[n].X / cell), (int)Math.Floor(pts[n].Y / cell));
            if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>();
            l.Add(n);
        }

        var used = new bool[pts.Count];
        var links = new List<(int A, int B)>();
        foreach (var n in dangling)
        {
            if (used[n]) continue;
            int best = -1;
            double bestD = maxGap;
            int gx = (int)Math.Floor(pts[n].X / cell);
            int gy = (int)Math.Floor(pts[n].Y / cell);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (!grid.TryGetValue((gx + dx, gy + dy), out var l)) continue;
                    foreach (var m in l)
                    {
                        if (m == n || used[m]) continue;
                        double d = pts[n].DistanceTo(pts[m]);
                        if (d <= bestD) { bestD = d; best = m; }
                    }
                }
            if (best >= 0)
            {
                used[n] = used[best] = true;
                links.Add((n, best));
            }
        }

        foreach (var (a, b) in links)
            segs.Add(new Seg(pts[a], pts[b]));
        return segs;
    }
}
