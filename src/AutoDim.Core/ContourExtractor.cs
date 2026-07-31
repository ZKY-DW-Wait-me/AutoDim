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

/// <summary>清洗结果。</summary>
public sealed record CleanResult(
    List<(Point2D A, Point2D B)> CleanedSegments,
    List<Point2D[]> Faces,
    List<(Point2D Center, double Radius)> UniqueCircles);

/// <summary>
/// 清洗管线入口：脏线段 -> 干净线段 -> 闭合面 + 去重圆。
/// </summary>
public static class ContourExtractor
{
    public static CleanResult Process(
        IEnumerable<(Point2D A, Point2D B)> segments,
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
            foreach (var face in faces)
            {
                for (int i = 0; i < face.Count; i++)
                {
                    var a = pts[face[i]];
                    var b = pts[face[(i + 1) % face.Count]];
                    faceKeys.Add(Canonical(a, b));
                }
            }

            var kept = new List<(Point2D A, Point2D B)>();
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
        var uniqueFaces = new List<Point2D[]>();
        var seenFaces = new HashSet<string>();
        foreach (var face in faces)
        {
            var key = string.Join("|", face.OrderBy(x => x));
            if (!seenFaces.Add(key)) continue;
            uniqueFaces.Add(face.Select(i => pts[i]).ToArray());
        }
        // 丢弃过小的碎环（填充笔触交叉产生的微型面，不是可标注特征）
        if (o.MinFaceArea > 0)
            uniqueFaces = uniqueFaces.Where(f => FaceArea(f) >= o.MinFaceArea).ToList();

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

    private static (Point2D, Point2D) Canonical(Point2D a, Point2D b) =>
        a.X < b.X || (a.X == b.X && a.Y <= b.Y) ? (a, b) : (b, a);

    /// <summary>有向多边形的带符号面积绝对值（鞋带公式）。</summary>
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
}
