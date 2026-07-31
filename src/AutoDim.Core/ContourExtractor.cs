using AutoDim.Core.Geometry;

namespace AutoDim.Core;

/// <summary>清洗参数。默认值针对毫米级图纸（碎片段 0.5mm 级）。</summary>
public sealed record CleanOptions(
    double SnapTol = 0.5,
    double MergeTol = 1.0,
    double AngleBucketDeg = 0.5,
    double NodeTol = 0.05,
    double MinKeepLength = 3.0,
    double MinFaceArea = 1.0);

/// <summary>一个闭合面：顶点 + 每边 bulge（非 0 表示圆弧边，AutoCAD bulge 约定）。</summary>
public sealed record FaceData(Point2D[] Points, double[] Bulges);

/// <summary>清洗结果。</summary>
public sealed record CleanResult(
    List<Seg> CleanedSegments,
    List<FaceData> Faces,
    List<(Point2D Center, double Radius)> UniqueCircles);

/// <summary>
/// 清洗管线入口：脏线段 -> 干净线段 -> 闭合面（带圆弧 bulge）+ 去重圆。
/// </summary>
public static class ContourExtractor
{
    public static CleanResult Process(
        IEnumerable<Seg> segments,
        IEnumerable<(Point2D Center, double Radius)> circles,
        CleanOptions? options = null)
    {
        var o = options ?? new CleanOptions();
        var cleaned = Cleaning.CleanSegments(segments, o.SnapTol, o.MergeTol, o.AngleBucketDeg);

        var (pts, faces) = PlanarGraph.FindFaces(cleaned, o.NodeTol);

        // 噪声过滤：保留"长度达标的线段"或"属于闭合面的边"，其余（填充笔触/碎渣）丢弃
        if (o.MinKeepLength > 0)
        {
            var faceKeys = new HashSet<(Point2D, Point2D)>();
            foreach (var (verts, _) in faces)
            {
                for (int i = 0; i < verts.Count; i++)
                {
                    var a = pts[verts[i]];
                    var b = pts[verts[(i + 1) % verts.Count]];
                    faceKeys.Add(Canonical(a, b));
                }
            }

            var kept = new List<Seg>();
            foreach (var s in cleaned)
            {
                if (s.A.DistanceTo(s.B) >= o.MinKeepLength)
                {
                    kept.Add(s);
                    continue;
                }
                var ka = Point2D.Snap(s.A, o.NodeTol);
                var kb = Point2D.Snap(s.B, o.NodeTol);
                if (faceKeys.Contains(Canonical(ka, kb)))
                    kept.Add(s);
            }
            if (kept.Count != cleaned.Count)
            {
                cleaned = kept;
                (pts, faces) = PlanarGraph.FindFaces(cleaned, o.NodeTol);
            }
        }

        // 面片去重（两个方向各出现一次，按顶点集合去重，保留首次）
        var uniqueFaces = new List<FaceData>();
        var seenFaces = new HashSet<string>();
        foreach (var (verts, bulges) in faces)
        {
            var key = string.Join("|", verts.OrderBy(x => x));
            if (!seenFaces.Add(key)) continue;
            uniqueFaces.Add(new FaceData(verts.Select(i => pts[i]).ToArray(), bulges.ToArray()));
        }
        // 丢弃过小的碎环（填充笔触交叉产生的微型面，不是可标注特征）
        if (o.MinFaceArea > 0)
            uniqueFaces = uniqueFaces.Where(f => FaceArea(f.Points) >= o.MinFaceArea).ToList();

        // 圆去重：同圆心 + 同半径(取整)视为重复
        var uniqueCircles = new List<(Point2D, double)>();
        var seenCircles = new HashSet<(Point2D, double)>();
        foreach (var (c, r) in circles)
        {
            var key = (Point2D.Snap(c, 0.2), Math.Round(r, 1));
            if (!seenCircles.Add(key)) continue;
            uniqueCircles.Add((c, r));
        }

        return new CleanResult(cleaned, uniqueFaces, uniqueCircles);
    }

    /// <summary>多边形面积（鞋带公式，按顶点折线计）。</summary>
    public static double FaceArea(IReadOnlyList<Point2D> face)
    {
        double area = 0;
        for (int i = 0; i < face.Count; i++)
        {
            var p1 = face[i];
            var p2 = face[(i + 1) % face.Count];
            area += p1.X * p2.Y - p2.X * p1.Y;
        }
        return Math.Abs(area) / 2.0;
    }

    private static (Point2D, Point2D) Canonical(Point2D a, Point2D b) =>
        a.X < b.X || (a.X == b.X && a.Y <= b.Y) ? (a, b) : (b, a);
}
