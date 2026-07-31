using AutoDim.Core;
using AutoDim.Core.Geometry;

int fails = 0;

void Check(bool cond, string name)
{
    Console.WriteLine((cond ? "PASS " : "FAIL ") + name);
    if (!cond) fails++;
}

static List<(Point2D, Point2D)> SquareEdges(Point2D[] sq, bool closed = true)
{
    var list = new List<(Point2D, Point2D)>();
    for (int i = 0; i < sq.Length - 1; i++)
        list.Add((sq[i], sq[i + 1]));
    if (closed)
        list.Add((sq[^1], sq[0]));
    return list;
}

var emptySegs = Array.Empty<(Point2D A, Point2D B)>();
var emptyCircles = Array.Empty<(Point2D Center, double Radius)>();

// 1) 正方形 -> 1 个闭合面，面积 100
var sq = new[] { new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10) };
var r1 = ContourExtractor.Process(SquareEdges(sq), emptyCircles);
Check(r1.Faces.Count == 1, "square -> 1 face");
Check(Math.Abs(ContourExtractor.FaceArea(r1.Faces[0]) - 100.0) < 1e-6, "square area = 100");

// 2) 带重复边 -> 仍 1 个面
var dup = SquareEdges(sq).Concat(new[] { (sq[0], sq[1]) }).ToList();
var r2 = ContourExtractor.Process(dup, emptyCircles);
Check(r2.Faces.Count == 1, "square+dup -> 1 face");

// 3) 微段拆分(每边 4 段) -> 合并后仍 1 个面
var micro = new List<(Point2D, Point2D)>();
for (int i = 0; i < 4; i++)
{
    var a = sq[i];
    var b = sq[(i + 1) % 4];
    for (int j = 0; j < 4; j++)
    {
        var p1 = new Point2D(a.X + (b.X - a.X) * j / 4.0, a.Y + (b.Y - a.Y) * j / 4.0);
        var p2 = new Point2D(a.X + (b.X - a.X) * (j + 1) / 4.0, a.Y + (b.Y - a.Y) * (j + 1) / 4.0);
        micro.Add((p1, p2));
    }
}
var r3 = ContourExtractor.Process(micro, emptyCircles);
Check(r3.Faces.Count == 1, "square-micro -> 1 face after merge");
Check(Math.Abs(ContourExtractor.FaceArea(r3.Faces[0]) - 100.0) < 1e-6, "micro face area = 100");

// 4) 外方 + 内方(孔) -> 2 个面
var inner = new[] { new Point2D(4, 4), new Point2D(6, 4), new Point2D(6, 6), new Point2D(4, 6) };
var r4 = ContourExtractor.Process(SquareEdges(sq).Concat(SquareEdges(inner)), emptyCircles);
Check(r4.Faces.Count == 2, "square+hole -> 2 faces");

// 5) 圆去重
var circles = new List<(Point2D, double)>
{
    (new Point2D(0, 0), 5.0),
    (new Point2D(0.01, 0.01), 5.0),
    (new Point2D(1, 1), 2.0),
};
var r5 = ContourExtractor.Process(emptySegs, circles);
Check(r5.UniqueCircles.Count == 2, "circle dedup -> 2 unique");

// 6) 噪声过滤：正方形上挂一根 0.5mm 短渣，清洗后应被丢弃，面保持 1 个
var noisy = SquareEdges(sq).Concat(new[] { (new Point2D(5, 0), new Point2D(5, 0.5)) }).ToList();
var r6 = ContourExtractor.Process(noisy, emptyCircles);
Check(r6.Faces.Count == 1, "square+noise -> 1 face");
Check(r6.CleanedSegments.Count == 4, "noise stub removed (cleaned=4)");

// 7) 小特征保护：2x2 小矩形(边 2mm < 3mm)因属于闭合面应被保留
var small = new[] { new Point2D(0, 0), new Point2D(2, 0), new Point2D(2, 2), new Point2D(0, 2) };
var r7 = ContourExtractor.Process(SquareEdges(small), emptyCircles);
Check(r7.Faces.Count == 1, "small square preserved by face membership");
Check(Math.Abs(ContourExtractor.FaceArea(r7.Faces[0]) - 4.0) < 1e-6, "small square area = 4");

// 8) 特征分组：两个分离的正方形 -> 2 组；距离小于 gap 时 -> 1 组
var sqB = new[] { new Point2D(20, 0), new Point2D(30, 0), new Point2D(30, 10), new Point2D(20, 10) };
var r8 = ContourExtractor.Process(SquareEdges(sq).Concat(SquareEdges(sqB)), emptyCircles);
var g8 = FeatureGrouping.GroupFeatures(r8.Faces, r8.UniqueCircles, 3.0);
Check(g8.Count == 2, "two separated squares -> 2 groups");
var g8b = FeatureGrouping.GroupFeatures(r8.Faces, r8.UniqueCircles, 15.0);
Check(g8b.Count == 1, "close squares (gap<=15) -> 1 group");

// 9) 方+孔+圆 -> 1 组，圆归入组
var r9 = ContourExtractor.Process(
    SquareEdges(sq).Concat(SquareEdges(inner)),
    new List<(Point2D, double)> { (new Point2D(5, 5), 1.0) });
var g9 = FeatureGrouping.GroupFeatures(r9.Faces, r9.UniqueCircles, 3.0);
Check(g9.Count == 1, "square+hole+circle -> 1 group");
Check(g9[0].CircleIndices.Count == 1, "circle joined the group");

Console.WriteLine(fails == 0 ? "== ALL PASS ==" : $"== {fails} FAILURE(S) ==");
return fails == 0 ? 0 : 1;
