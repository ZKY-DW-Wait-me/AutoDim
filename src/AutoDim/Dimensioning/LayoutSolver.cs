using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoDim.Cad;

namespace AutoDim.Dimensioning;

/// <summary>
/// 标注布局收尾：去重 + 外推避让。
/// - 去重：同一测量几何 + 同一尺寸线位置的完全重复标注只留一个；外推后仍重叠的
///   近重复(同一测量段 ≤1.5mm)只保留测量值较大者。来源：相邻面共享边、重复面被两个面各标一次。
/// - 外推：仍重叠的标注沿各自"远离被测要素"的方向逐轮外推（尺寸线/引线外移），
///   外推总量受 2.5×原始偏移 与 6×baseGap 双上限约束，配合 ADIMCLEAN 整区 6×gap 清场
///   缓冲区，保证下次运行可完整重生成（幂等）。
/// 只操作带 AUTODIM 标记的自产标注，绝不碰用户手标。
/// </summary>
internal static class LayoutSolver
{
    private const double PtTol = 0.25;      // 测量点去重容差(mm)
    private const double LineTol = 0.5;     // 尺寸线/引线位置去重容差(mm)
    private const double RotTol = 0.05;     // Rotated 旋转角去重容差(rad)
    private const int MaxRounds = 40;       // 外推最大轮数(每轮处理全部重叠对)
    private const double OffsetCapFactor = 2.5; // 外推量上限 = 该标注原始偏移的倍数
    private const double NearDupTol = 1.5;  // 近重复测量段容差(mm)：吃掉脏几何/重复面产生的碎片重复标注

    /// <summary>整图去重（不移动任何标注；供全局收尾调用）。</summary>
    public static int Dedupe(Database db, Transaction tr)
    {
        var ids = Collect(db, tr, null, 0.0);
        return RemoveDuplicates(tr, ids);
    }

    /// <summary>区域内去重 + 外推避让 + 近重复抑制。返回 (去重数(含近重复), 外推次数)。</summary>
    public static (int removed, int moved) Resolve(
        Database db, Transaction tr, Extents3d region, double baseGap)
    {
        var ids = Collect(db, tr, region, 4.0 * baseGap);
        int removed = RemoveDuplicates(tr, ids);
        ids = Collect(db, tr, region, 4.0 * baseGap);
        int moved = Spread(tr, ids, baseGap);
        int near = SuppressNearDuplicates(tr, ids);
        return (removed + near, moved);
    }

    // ---------- 收集 ----------

    private static List<ObjectId> Collect(Database db, Transaction tr, Extents3d? region, double margin)
    {
        var result = new List<ObjectId>();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        Extents3d? box = null;
        if (region.HasValue)
        {
            var r = region.Value;
            box = new Extents3d(
                new Point3d(r.MinPoint.X - margin, r.MinPoint.Y - margin, 0),
                new Point3d(r.MaxPoint.X + margin, r.MaxPoint.Y + margin, 0));
        }
        foreach (ObjectId id in ms)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Dimension d) continue;
            if (d.Layer != "ADIM" || !AdimMarker.IsMarked(d)) continue;
            if (box.HasValue)
            {
                try
                {
                    var e = d.GeometricExtents;
                    if (!Intersects(e, box.Value)) continue;
                }
                catch { continue; }
            }
            result.Add(id);
        }
        return result;
    }

    // ---------- 去重 ----------

    private static int RemoveDuplicates(Transaction tr, List<ObjectId> ids)
    {
        int removed = 0;
        var dead = new HashSet<ObjectId>();
        for (int i = 0; i < ids.Count; i++)
        {
            if (dead.Contains(ids[i])) continue;
            Dimension? a = OpenDim(tr, ids[i]);
            if (a == null) continue;
            for (int j = i + 1; j < ids.Count; j++)
            {
                if (dead.Contains(ids[j])) continue;
                Dimension? b = OpenDim(tr, ids[j]);
                if (b == null) continue;
                if (SameDimension(a, b))
                {
                    dead.Add(ids[j]);
                    removed++;
                }
            }
        }
        foreach (var id in dead)
            if (tr.GetObject(id, OpenMode.ForWrite) is Entity e) e.Erase();
        return removed;
    }

    private static Dimension? OpenDim(Transaction tr, ObjectId id)
    {
        try { return tr.GetObject(id, OpenMode.ForRead) as Dimension; }
        catch { return null; }
    }

    private static bool SameDimension(Dimension a, Dimension b)
    {
        switch (a)
        {
            case AlignedDimension x when b is AlignedDimension y:
                return (SamePt(x.XLine1Point, y.XLine1Point) && SamePt(x.XLine2Point, y.XLine2Point) ||
                        SamePt(x.XLine1Point, y.XLine2Point) && SamePt(x.XLine2Point, y.XLine1Point)) &&
                       SamePt(x.DimLinePoint, y.DimLinePoint);
            case RotatedDimension x when b is RotatedDimension y:
                return (SamePt(x.XLine1Point, y.XLine1Point) && SamePt(x.XLine2Point, y.XLine2Point) ||
                        SamePt(x.XLine1Point, y.XLine2Point) && SamePt(x.XLine2Point, y.XLine1Point)) &&
                       System.Math.Abs(x.Rotation - y.Rotation) < RotTol &&
                       SamePt(x.DimLinePoint, y.DimLinePoint);
            case RadialDimension x when b is RadialDimension y:
                return SamePt(x.Center, y.Center) && SamePt(x.ChordPoint, y.ChordPoint) &&
                       System.Math.Abs(x.LeaderLength - y.LeaderLength) < LineTol;
            default:
                return false;
        }
    }

    private static bool SamePt(Point3d p, Point3d q) =>
        p.DistanceTo(q) < PtTol;

    // ---------- 外推避让 ----------

    private static int Spread(Transaction tr, List<ObjectId> ids, double baseGap)
    {
        if (ids.Count < 2) return 0;
        int moved = 0;
        var blocked = new HashSet<long>();
        var origOffsets = new Dictionary<ObjectId, double>();
        for (int round = 0; round < MaxRounds; round++)
        {
            var exts = ReadExtents(tr, ids);
            int pushedThisRound = 0;
            for (int i = 0; i < ids.Count; i++)
                for (int j = i + 1; j < ids.Count; j++)
                {
                    if (blocked.Contains((long)i * ids.Count + j)) continue;
                    double ar = OverlapArea(exts[i], exts[j]);
                    if (ar <= 0) continue;

                    var mi = GetMoveInfo(tr, ids[i], origOffsets);
                    var mj = GetMoveInfo(tr, ids[j], origOffsets);
                    if (mi == null || mj == null)
                    {
                        blocked.Add((long)i * ids.Count + j);
                        continue;
                    }
                    // 推 offset 较大者（并列推先出现的）；到上限则换另一个
                    int moverIdx = mi.Value.Offset >= mj.Value.Offset ? i : j;
                    if (TryPush(tr, ids[moverIdx], moverIdx == i ? mi.Value : mj.Value, baseGap))
                    {
                        pushedThisRound++;
                        moved++;
                        continue;
                    }
                    int otherIdx = moverIdx == i ? j : i;
                    if (TryPush(tr, ids[otherIdx], otherIdx == i ? mi.Value : mj.Value, baseGap))
                    {
                        pushedThisRound++;
                        moved++;
                        continue;
                    }
                    blocked.Add((long)i * ids.Count + j);
                }
            if (pushedThisRound == 0) break;
        }
        return moved;
    }

    /// <summary>外推后仍重叠的"近重复"标注（同一测量段、相距 ≤1.5mm，来自重复/重叠面
    /// 被两次标注）只保留测量值较大者。</summary>
    private static int SuppressNearDuplicates(Transaction tr, List<ObjectId> ids)
    {
        if (ids.Count < 2) return 0;
        var exts = ReadExtents(tr, ids);
        var dead = new HashSet<ObjectId>();
        for (int i = 0; i < ids.Count; i++)
        {
            if (dead.Contains(ids[i])) continue;
            Dimension? a = OpenDim(tr, ids[i]);
            if (a == null) continue;
            for (int j = i + 1; j < ids.Count; j++)
            {
                if (dead.Contains(ids[j])) continue;
                if (OverlapArea(exts[i], exts[j]) <= 0) continue;
                Dimension? b = OpenDim(tr, ids[j]);
                if (b == null) continue;
                if (!SameMeasuredSegmentNear(a, b, NearDupTol)) continue;
                double va = TextValue(a), vb = TextValue(b);
                // 保留测量值较大者(较长边更接近真实几何)，删除较小者
                ObjectId drop = va >= vb ? ids[j] : ids[i];
                dead.Add(drop);
            }
        }
        foreach (var id in dead)
            if (tr.GetObject(id, OpenMode.ForWrite) is Entity e) e.Erase();
        return dead.Count;
    }

    private static List<Extents3d?> ReadExtents(Transaction tr, List<ObjectId> ids)
    {
        var exts = new List<Extents3d?>(ids.Count);
        foreach (var id in ids)
        {
            Dimension? d = OpenDim(tr, id);
            Extents3d? e = null;
            if (d != null)
            {
                try { e = d.GeometricExtents; }
                catch { }
            }
            exts.Add(e);
        }
        return exts;
    }

    /// <summary>两个标注是否"同一测量段"（端点两两互换在容差内，仅直线类）。</summary>
    private static bool SameMeasuredSegmentNear(Dimension a, Dimension b, double tol)
    {
        Point3d? a1 = null, a2 = null, b1 = null, b2 = null;
        switch (a)
        {
            case AlignedDimension ad: a1 = ad.XLine1Point; a2 = ad.XLine2Point; break;
            case RotatedDimension rd: a1 = rd.XLine1Point; a2 = rd.XLine2Point; break;
            default: return false;
        }
        switch (b)
        {
            case AlignedDimension ad: b1 = ad.XLine1Point; b2 = ad.XLine2Point; break;
            case RotatedDimension rd: b1 = rd.XLine1Point; b2 = rd.XLine2Point; break;
            default: return false;
        }
        if (a1 == null || a2 == null || b1 == null || b2 == null) return false;
        var p1 = a1.Value; var p2 = a2.Value; var q1 = b1.Value; var q2 = b2.Value;
        return (p1.DistanceTo(q1) < tol && p2.DistanceTo(q2) < tol) ||
               (p1.DistanceTo(q2) < tol && p2.DistanceTo(q1) < tol);
    }

    /// <summary>解析标注文字里的数值（去掉 ⌀ 前缀），失败返回 0。</summary>
    private static double TextValue(Dimension d)
    {
        string t = d.DimensionText ?? "";
        t = t.Replace("%%c", "").Replace("Ø", "").Trim();
        int i = 0;
        while (i < t.Length && (char.IsDigit(t[i]) || t[i] == '.')) i++;
        if (i == 0) return 0;
        return double.TryParse(t.Substring(0, i), System.Globalization.NumberStyles.Any,
                               System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private readonly record struct MoveInfo(Point3d Base, Vector3d Dir, double Offset, double Offset0);

    private static MoveInfo? GetMoveInfo(Transaction tr, ObjectId id, Dictionary<ObjectId, double> origOffsets)
    {
        Dimension? d = OpenDim(tr, id);
        if (d == null) return null;
        switch (d)
        {
            case AlignedDimension ad:
                return MoveInfoOf(ad.XLine1Point, ad.XLine2Point, ad.DimLinePoint, id, origOffsets);
            case RotatedDimension rd:
                return MoveInfoOf(rd.XLine1Point, rd.XLine2Point, rd.DimLinePoint, id, origOffsets);
            case RadialDimension rdd:
            {
                var v = rdd.ChordPoint - rdd.Center;
                double len = v.Length;
                if (len < 1e-9) return null;
                double off0 = origOffsets.TryGetValue(id, out var o0) ? o0 : rdd.LeaderLength;
                origOffsets[id] = off0;
                return new MoveInfo(rdd.Center, v / len, rdd.LeaderLength, off0);
            }
            default:
                return null;
        }
    }

    private static MoveInfo? MoveInfoOf(Point3d p1, Point3d p2, Point3d dl, ObjectId id,
        Dictionary<ObjectId, double> origOffsets)
    {
        var mid = new Point3d((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2, 0);
        var v = dl - mid;
        double len = v.Length;
        if (len < 1e-9) return null;
        double off0 = origOffsets.TryGetValue(id, out var o0) ? o0 : len;
        origOffsets[id] = off0;
        return new MoveInfo(mid, v / len, len, off0);
    }

    /// <summary>把标注沿远离被测要素的方向外推一步；到上限或无法移动返回 false。</summary>
    private static bool TryPush(Transaction tr, ObjectId id, MoveInfo m, double baseGap)
    {
        if (m.Offset < 1e-6) return false;
        double cap = System.Math.Min(m.Offset0 * OffsetCapFactor, 6.0 * baseGap);
        if (m.Offset + 1e-6 >= cap) return false;
        double step = System.Math.Max(0.8, 0.5 * m.Offset);
        double target = System.Math.Min(cap, m.Offset + step);
        if (target - m.Offset < 0.2) return false;
        if (tr.GetObject(id, OpenMode.ForWrite) is not Dimension d) return false;
        try
        {
            switch (d)
            {
                case AlignedDimension ad:
                    ad.DimLinePoint = m.Base + m.Dir * target;
                    break;
                case RotatedDimension rd:
                    rd.DimLinePoint = m.Base + m.Dir * target;
                    break;
                case RadialDimension rdd:
                    rdd.LeaderLength = target;
                    break;
                default:
                    return false;
            }
            d.RecomputeDimensionBlock(true);
        }
        catch { return false; }
        return true;
    }

    // ---------- 几何工具 ----------

    private static double OverlapArea(Extents3d? a, Extents3d? b)
    {
        if (a == null || b == null) return 0;
        var x = a.Value; var y = b.Value;
        double ix = System.Math.Min(x.MaxPoint.X, y.MaxPoint.X) - System.Math.Max(x.MinPoint.X, y.MinPoint.X);
        double iy = System.Math.Min(x.MaxPoint.Y, y.MaxPoint.Y) - System.Math.Max(x.MinPoint.Y, y.MinPoint.Y);
        if (ix <= 0.01 || iy <= 0.01) return 0;
        return ix * iy;
    }

    private static bool Intersects(Extents3d a, Extents3d b) =>
        !(a.MaxPoint.X < b.MinPoint.X || a.MinPoint.X > b.MaxPoint.X ||
          a.MaxPoint.Y < b.MinPoint.Y || a.MinPoint.Y > b.MaxPoint.Y);
}
