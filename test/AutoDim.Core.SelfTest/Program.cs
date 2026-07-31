using AutoDim.Core;
using AutoDim.Core.Geometry;

int fails = 0;

void Check(bool cond, string name)
{
    Console.WriteLine((cond ? "PASS " : "FAIL ") + name);
    if (!cond) fails++;
}

static List<Seg> Edges(Point2D[] pts, bool closed = true)
{
    var list = new List<Seg>();
    for (int i = 0; i < pts.Length - 1; i++)
        list.Add(new Seg(pts[i], pts[i + 1]));
    if (closed)
        list.Add(new Seg(pts[^1], pts[0]));
    return list;
}

var emptyCircles = Array.Empty<(Point2D Center, double Radius)>();

// 1) 正方形 -> 1 个闭合面，面积 100
var sq = new[] { new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10) };
var r1 = ContourExtractor.Process(Edges(sq), emptyCircles);
Check(r1.Faces.Count == 1, "square -> 1 face");
Check(Math.Abs(ContourExtractor.FaceArea(r1.Faces[0].Points) - 100.0) < 1e-6, "square area = 100");

// 2) 带重复边 -> 仍 1 个面
var dup = Edges(sq).Concat(new[] { new Seg(sq[0], sq[1]) }).ToList();
var r2 = ContourExtractor.Process(dup, emptyCircles);
Check(r2.Faces.Count == 1, "square+dup -> 1 face");

// 3) 微段拆分(每边 4 段) -> 合并后仍 1 个面
var micro = new List<Seg>();
for (int i = 0; i < 4; i++)
{
    var a = sq[i];
    var b = sq[(i + 1) % 4];
    for (int j = 0; j < 4; j++)
    {
        var p1 = new Point2D(a.X + (b.X - a.X) * j / 4.0, a.Y + (b.Y - a.Y) * j / 4.0);
        var p2 = new Point2D(a.X + (b.X - a.X) * (j + 1) / 4.0, a.Y + (b.Y - a.Y) * (j + 1) / 4.0);
        micro.Add(new Seg(p1, p2));
    }
}
var r3 = ContourExtractor.Process(micro, emptyCircles);
Check(r3.Faces.Count == 1, "square-micro -> 1 face after merge");
Check(Math.Abs(ContourExtractor.FaceArea(r3.Faces[0].Points) - 100.0) < 1e-6, "micro face area = 100");

// 4) 外方 + 内方(孔) -> 2 个面
var inner = new[] { new Point2D(4, 4), new Point2D(6, 4), new Point2D(6, 6), new Point2D(4, 6) };
var r4 = ContourExtractor.Process(Edges(sq).Concat(Edges(inner)), emptyCircles);
Check(r4.Faces.Count == 2, "square+hole -> 2 faces");

// 5) 圆去重
var circles = new List<(Point2D, double)>
{
    (new Point2D(0, 0), 5.0),
    (new Point2D(0.01, 0.01), 5.0),
    (new Point2D(1, 1), 2.0),
};
var r5 = ContourExtractor.Process(Array.Empty<Seg>(), circles);
Check(r5.UniqueCircles.Count == 2, "circle dedup -> 2 unique");

// 6) 噪声过滤：正方形上挂一根 0.5mm 短渣，清洗后应被丢弃，面保持 1 个
var noisy = Edges(sq).Concat(new[] { new Seg(new Point2D(5, 0), new Point2D(5, 0.5)) }).ToList();
var r6 = ContourExtractor.Process(noisy, emptyCircles);
Check(r6.Faces.Count == 1, "square+noise -> 1 face");
Check(r6.CleanedSegments.Count == 4, "noise stub removed (cleaned=4)");

// 7) 小特征保护：2x2 小矩形(边 2mm < 3mm)因属于闭合面应被保留
var small = new[] { new Point2D(0, 0), new Point2D(2, 0), new Point2D(2, 2), new Point2D(0, 2) };
var r7 = ContourExtractor.Process(Edges(small), emptyCircles);
Check(r7.Faces.Count == 1, "small square preserved by face membership");
Check(Math.Abs(ContourExtractor.FaceArea(r7.Faces[0].Points) - 4.0) < 1e-6, "small square area = 4");

// 8) 特征分组：两个分离的正方形 -> 2 组；距离小于 gap 时 -> 1 组
var sqB = new[] { new Point2D(20, 0), new Point2D(30, 0), new Point2D(30, 10), new Point2D(20, 10) };
var r8 = ContourExtractor.Process(Edges(sq).Concat(Edges(sqB)), emptyCircles);
var g8 = FeatureGrouping.GroupFeatures(r8.Faces, r8.UniqueCircles, 3.0);
Check(g8.Count == 2, "two separated squares -> 2 groups");
var g8b = FeatureGrouping.GroupFeatures(r8.Faces, r8.UniqueCircles, 15.0);
Check(g8b.Count == 1, "close squares (gap<=15) -> 1 group");

// 9) 方+孔+圆 -> 1 组，圆归入组
var r9 = ContourExtractor.Process(
    Edges(sq).Concat(Edges(inner)),
    new List<(Point2D, double)> { (new Point2D(5, 5), 1.0) });
var g9 = FeatureGrouping.GroupFeatures(r9.Faces, r9.UniqueCircles, 3.0);
Check(g9.Count == 1, "square+hole+circle -> 1 group");
Check(g9[0].CircleIndices.Count == 1, "circle joined the group");

// 10) 圆弧保留：正方形带 90° 圆角(圆心(8,8) 半径2)，闭合面应含一条 bulge≈tan(π/8) 的弧边
var fillet = new List<Seg>
{
    new(new Point2D(0, 0), new Point2D(10, 0)),
    new(new Point2D(10, 0), new Point2D(10, 8)),
    new(new Point2D(10, 8), new Point2D(8, 10), Math.Tan(Math.PI / 8)),
    new(new Point2D(8, 10), new Point2D(0, 10)),
    new(new Point2D(0, 10), new Point2D(0, 0)),
};
var r10 = ContourExtractor.Process(fillet, emptyCircles);
Check(r10.Faces.Count == 1, "fillet square -> 1 face");
Check(Math.Abs(ContourExtractor.FaceArea(r10.Faces[0].Points) - 98.0) < 1e-6, "fillet chord area = 98");
bool hasArc = r10.Faces[0].Bulges.Any(b => Math.Abs(Math.Abs(b) - Math.Tan(Math.PI / 8)) < 0.01);
Check(hasArc, "arc bulge preserved (~0.4142)");

// 11) 细长碎面过滤：20x0.6 长条（周长²/面积≈141 > 60）应被丢弃，正方形(16)保留
var sliver = new[] { new Point2D(0, 0), new Point2D(20, 0), new Point2D(20, 0.6), new Point2D(0, 0.6) };
var r11 = ContourExtractor.Process(Edges(sliver), emptyCircles);
Check(r11.Faces.Count == 0, "thin sliver face dropped");
var r11b = ContourExtractor.Process(Edges(sq), emptyCircles);
Check(r11b.Faces.Count == 1, "square kept by thinness filter");

Console.WriteLine(fails == 0 ? "== ALL PASS ==" : $"== {fails} FAILURE(S) ==");
return fails == 0 ? 0 : 1;
