namespace AutoDim.Core.Geometry;

/// <summary>
/// 平面图面片追踪：把清洗后的线段建成平面图，用"最小右转"规则追踪有界闭合面。
/// 每个有界面会出现两个方向，由调用方去重。
/// </summary>
public static class PlanarGraph
{
    public static (List<Point2D> Points, List<List<int>> Faces) FindFaces(
        IEnumerable<(Point2D A, Point2D B)> segments,
        double nodeTol = 0.05)
    {
        var nodes = new Dictionary<Point2D, int>();
        var edges = new List<(int A, int B)>();
        foreach (var (a, b) in segments)
        {
            var ka = Point2D.Snap(a, nodeTol);
            var kb = Point2D.Snap(b, nodeTol);
            if (ka == kb) continue;
            if (!nodes.TryGetValue(ka, out int ia)) { ia = nodes.Count; nodes[ka] = ia; }
            if (!nodes.TryGetValue(kb, out int ib)) { ib = nodes.Count; nodes[kb] = ib; }
            edges.Add((ia, ib));
        }

        var pts = nodes.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        var adj = new Dictionary<int, List<int>>();
        foreach (var (a, b) in edges)
        {
            AddNeighbor(adj, a, b);
            AddNeighbor(adj, b, a);
        }

        double Angle(int u, int v) =>
            Math.Atan2(pts[v].Y - pts[u].Y, pts[v].X - pts[u].X);

        var outAngles = adj.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(v => (Ang: Angle(kv.Key, v), V: v))
                          .OrderBy(x => x.Ang).ToList());

        var visited = new HashSet<(int, int)>();
        var faces = new List<List<int>>();
        foreach (var (a, b) in edges)
        {
            foreach (var start in new[] { (a, b), (b, a) })
            {
                if (visited.Contains(start)) continue;
                var path = new List<(int, int)>();
                var cur = start;
                while (!visited.Contains(cur))
                {
                    visited.Add(cur);
                    path.Add(cur);
                    double rev = Angle(cur.Item2, cur.Item1);
                    var al = outAngles[cur.Item2];
                    int idx = FirstGe(al, rev);
                    int nxt = al[(idx - 1 + al.Count) % al.Count].V;
                    cur = (cur.Item2, nxt);
                    if (path.Count > edges.Count * 2 + 4) break;
                }
                if (path.Count >= 3 && cur == start)
                    faces.Add(path.Select(p => p.Item2).ToList());
            }
        }

        return (pts, faces);
    }

    private static void AddNeighbor(Dictionary<int, List<int>> adj, int u, int v)
    {
        if (!adj.TryGetValue(u, out var list))
            adj[u] = list = new List<int>();
        list.Add(v);
    }

    /// <summary>第一个角度 &gt;= rev 的下标（bisect_left 语义）。</summary>
    private static int FirstGe(List<(double Ang, int V)> al, double rev)
    {
        int lo = 0, hi = al.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (al[mid].Ang < rev) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
