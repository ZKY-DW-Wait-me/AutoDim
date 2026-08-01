namespace AutoDim.Core.Geometry;

/// <summary>
/// 平面图面片追踪：把清洗后的线段建成平面图，用"最小右转"规则追踪有界闭合面。
/// 每个有界面会出现两个方向，由调用方去重。面边携带 bulge（圆弧信息）。
/// </summary>
public static class PlanarGraph
{
    public static (List<Point2D> Points, List<(List<int> Vertices, List<double> Bulges)> Faces) FindFaces(
        IEnumerable<Seg> segments,
        double nodeTol = 0.05)
    {
        var nodes = new Dictionary<Point2D, int>();
        var edges = new List<(int A, int B, double Bulge)>();
        foreach (var s in segments)
        {
            var ka = Point2D.Snap(s.A, nodeTol);
            var kb = Point2D.Snap(s.B, nodeTol);
            if (ka == kb) continue;
            if (!nodes.TryGetValue(ka, out int ia)) { ia = nodes.Count; nodes[ka] = ia; }
            if (!nodes.TryGetValue(kb, out int ib)) { ib = nodes.Count; nodes[kb] = ib; }
            edges.Add((ia, ib, s.Bulge));
        }

        var pts = nodes.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        var edgeMap = new Dictionary<(int, int), double>();
        foreach (var (a, b, bl) in edges) edgeMap[(a, b)] = bl;

        var adj = new Dictionary<int, List<int>>();
        foreach (var (a, b, _) in edges)
        {
            AddNeighbor(adj, a, b);
            AddNeighbor(adj, b, a);
        }

        // 圆弧边按"端点处切线方向"参与转向判断，不能当直线(弦方向)处理——
        // 对 77° 大弧，弦方向与切线差 ~38°，最小右转会选错下一条边，
        // 导致弧的 bulge 被贴到相邻直线上(端头 R6 被标成 R5.69/R6.42 的另一半根因)。
        double TangentOut(int u, int v)
        {
            double phi = Math.Atan2(pts[v].Y - pts[u].Y, pts[v].X - pts[u].X);
            double b = EdgeBulge(u, v, edgeMap);
            if (Math.Abs(b) > 1e-9)
                phi += 2.0 * Math.Atan(b);   // 起点切线比弦偏 2·atan(b)
            return phi;
        }

        double TangentIn(int u, int v)
        {
            // 在 u 点，边 v→u 的来向(从 u 指向 v 的切线方向)：
            // 直线 = atan2(v-u)；弧再加 2·atan(b_uv)(b_uv=EdgeBulge(u,v)，方向反转已取负)
            // 半圆验证：u=(2,0) v=(0,0) 存储边(0,0)->(2,0) b=+1，EdgeBulge(u,v)=-1，
            // 来向 = π + 2·atan(-1) = π/2 = +90° ✅(从 (2,0) 沿弧回 (0,0) 向上)
            double phi = Math.Atan2(pts[v].Y - pts[u].Y, pts[v].X - pts[u].X);
            double b = EdgeBulge(u, v, edgeMap);
            if (Math.Abs(b) > 1e-9)
                phi += 2.0 * Math.Atan(b);
            return phi;
        }

        var outAngles = adj.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(v => (Ang: TangentOut(kv.Key, v), V: v))
                          .OrderBy(x => x.Ang).ToList());

        var visited = new HashSet<(int, int)>();
        var faces = new List<(List<int> Vertices, List<double> Bulges)>();
        foreach (var (a, b, _) in edges)
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
                    double rev = TangentIn(cur.Item2, cur.Item1);
                    var al = outAngles[cur.Item2];
                    int idx = FirstGe(al, rev);
                    int nxt = al[(idx - 1 + al.Count) % al.Count].V;
                    cur = (cur.Item2, nxt);
                    if (path.Count > edges.Count * 2 + 4) break;
                }
                if (path.Count >= 3 && cur == start)
                {
                    var verts = path.Select(p => p.Item2).ToList();
                    // bulge[k] 必须对应"边 verts[k]->verts[k+1]"：path[k] 是边 verts[k-1]->verts[k]，
                    // 所以取 path[k+1] 的 bulge（闭合循环）。之前直接取 path[k] 造成错位一位，
                    // 圆弧 bulge 被贴到相邻边上(端头 R6 被标成 R5.69/R6.42 的根因)。
                    var bulges = new List<double>(path.Count);
                    for (int k = 0; k < path.Count; k++)
                    {
                        var p = path[(k + 1) % path.Count];
                        bulges.Add(edgeMap.TryGetValue(p, out var bl) ? bl : -edgeMap[(p.Item2, p.Item1)]);
                    }
                    faces.Add((verts, bulges));
                }
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

    /// <summary>边 (u,v) 的 bulge；存储方向相反时取负。</summary>
    private static double EdgeBulge(int u, int v, Dictionary<(int, int), double> edgeMap)
    {
        if (edgeMap.TryGetValue((u, v), out var b)) return b;
        if (edgeMap.TryGetValue((v, u), out var b2)) return -b2;
        return 0;
    }
}
