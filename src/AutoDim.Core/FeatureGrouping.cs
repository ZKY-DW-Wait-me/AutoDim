using AutoDim.Core.Geometry;

namespace AutoDim.Core;

/// <summary>一个特征组：若干闭合面 + 若干圆（视为同一零件/特征簇，一起标注）。</summary>
public sealed record FeatureGroup(List<int> FaceIndices, List<int> CircleIndices);

/// <summary>
/// 特征分组：把闭合面与圆按空间邻近聚成组。
/// 两个特征的外包框距离 &lt;= gapTol 视为同一组（同一张图里的多个零件分开标注，避免尺寸互相打架）。
/// </summary>
public static class FeatureGrouping
{
    public static List<FeatureGroup> GroupFeatures(
        IReadOnlyList<Point2D[]> faces,
        IReadOnlyList<(Point2D Center, double Radius)> circles,
        double gapTol)
    {
        int n = faces.Count + circles.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[rb] = ra;
        }

        var boxes = new List<(double X0, double Y0, double X1, double Y1)>(n);
        foreach (var face in faces)
            boxes.Add(FaceBox(face));
        foreach (var (c, r) in circles)
            boxes.Add((c.X - r, c.Y - r, c.X + r, c.Y + r));

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (BBoxDist(boxes[i], boxes[j]) <= gapTol)
                    Union(i, j);

        var groups = new Dictionary<int, FeatureGroup>();
        for (int i = 0; i < faces.Count; i++)
        {
            int r = Find(i);
            if (!groups.TryGetValue(r, out var g))
                groups[r] = g = new FeatureGroup(new List<int>(), new List<int>());
            g.FaceIndices.Add(i);
        }
        for (int i = 0; i < circles.Count; i++)
        {
            int r = Find(faces.Count + i);
            if (!groups.TryGetValue(r, out var g))
                groups[r] = g = new FeatureGroup(new List<int>(), new List<int>());
            g.CircleIndices.Add(i);
        }
        return groups.Values.ToList();
    }

    private static (double X0, double Y0, double X1, double Y1) FaceBox(Point2D[] face)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue;
        double x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in face)
        {
            x0 = Math.Min(x0, p.X);
            y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X);
            y1 = Math.Max(y1, p.Y);
        }
        return (x0, y0, x1, y1);
    }

    private static double BBoxDist(
        (double X0, double Y0, double X1, double Y1) a,
        (double X0, double Y0, double X1, double Y1) b)
    {
        double dx = Math.Max(0, Math.Max(a.X0 - b.X1, b.X0 - a.X1));
        double dy = Math.Max(0, Math.Max(a.Y0 - b.Y1, b.Y0 - a.Y1));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
