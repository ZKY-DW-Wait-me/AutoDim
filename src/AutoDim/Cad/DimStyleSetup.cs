using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoDim.Cad;

/// <summary>
/// 建一个专属标注样式 "ADIM"，把文字/箭头/位置等变量固化进去；插件标注统一用它，
/// 不依赖也不污染用户全局标注变量。按包围盒自适应 DIMSCALE(写在样式里)。
/// 用户可用 ADIMSCALE 命令固定一个 scale 值(持久化到 NOD)，固定后不再自适应。
/// </summary>
internal static class DimStyleSetup
{
    public const string StyleName = "ADIM";
    private const string NodKey = "AutoDim_UserScale";   // 存在 NOD 里的键名

    /// <summary>
    /// 创建或更新 "ADIM" 标注样式，返回其 ObjectId。
    /// scale 优先级：用户固定值(ADIMSCALE 设的) > 自适应包围盒。
    /// </summary>
    public static ObjectId EnsureStyle(Database db, Transaction tr, Extents3d? ext)
    {
        double scale = ResolveScale(db, tr, ext);
        ObjectId textStyleId = EnsureTextStyle(db, tr);

        var dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
        ObjectId id;
        if (dst.Has(StyleName))
        {
            id = dst[StyleName];
            var rec = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForWrite);
            ApplyValues(db, tr, rec, scale);
        }
        else
        {
            dst.UpgradeOpen();
            var rec = new DimStyleTableRecord { Name = StyleName };
            ApplyValues(db, tr, rec, scale);
            id = dst.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }
        return id;
    }

    /// <summary>创建/复用 ADIM 专用文字样式：国标字体 gbeitc.shx(西文)+gbcbig.shx(中文)，
    /// 避免默认 Standard/txt.shx 显示 ⌀/汉字走样。</summary>
    private static ObjectId EnsureTextStyle(Database db, Transaction tr)
    {
        const string styleName = "AutoDimText";
        var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        if (tst.Has(styleName))
            return tst[styleName];
        tst.UpgradeOpen();
        var rec = new TextStyleTableRecord
        {
            Name = styleName,
            FileName = "gbeitc.shx",
            BigFontFileName = "gbcbig.shx",
        };
        ObjectId id = tst.Add(rec);
        tr.AddNewlyCreatedDBObject(rec, true);
        return id;
    }

    /// <summary>解析 Dimscale：优先用户固定值，否则按【选集中各实体包围盒最大边的"中位数"】自适应。
    /// 取中位数而非合并最大值：用户常把两个相距很远的图形一起标注，合并包围盒会把"两图之间的空白距离"
    /// 也算进去，scale 被拉到 3.0 上限、字号爆大。中位数跟随"典型那一张"的尺寸，远的那张不影响比例。
    /// ext 传合并包围盒(用于 fallback)，真正的中位数从 ids 各实体的 GeometricExtents 算。</summary>
    private static double ResolveScale(Database db, Transaction tr, Extents3d? ext)
    {
        double? userScale = ReadUserScale(db, tr);
        if (userScale.HasValue && userScale.Value > 0)
            return userScale.Value;

        // 收集选集里每个实体自己包围盒的最大边长，取中位数
        var edges = new List<double>();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        if (bt.Has(BlockTableRecord.ModelSpace))
        {
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                Entity? ent;
                try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                catch { continue; }
                if (ent == null) continue;
                Extents3d ge;
                try { ge = ent.GeometricExtents; }
                catch { continue; }
                double w = ge.MaxPoint.X - ge.MinPoint.X;
                double h = ge.MaxPoint.Y - ge.MinPoint.Y;
                double e = System.Math.Max(w, h);
                if (e > 1e-6) edges.Add(e);
            }
        }
        if (edges.Count > 0)
        {
            edges.Sort();
            double med = edges[edges.Count / 2];
            // 字高下限：国标标注文字不小于 2.5mm(Dimtxt=2.5 × scale)，scale 最小 1.0——
            // 否则小零件比例被压到 0.5，字高仅 1.25mm，"1/7" 等数字难以分辨
            return System.Math.Clamp(med / 100.0, 1.0, 3.0);
        }
        if (ext != null)
        {
            double w = ext.Value.MaxPoint.X - ext.Value.MinPoint.X;
            double h = ext.Value.MaxPoint.Y - ext.Value.MinPoint.Y;
            double maxEdge = System.Math.Max(w, h);
            if (maxEdge > 1e-6)
                return System.Math.Clamp(maxEdge / 100.0, 1.0, 3.0);
        }
        return 1.0;
    }

    /// <summary>把用户固定的 scale 写进 NOD 持久化。传 null 清除(恢复自适应)。</summary>
    public static void SetUserScale(Database db, double? scale)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (scale == null || scale.Value <= 0)
        {
            if (nod.Contains(NodKey))
            {
                ObjectId id = nod.GetAt(NodKey);
                tr.GetObject(id, OpenMode.ForWrite).Erase();
            }
        }
        else
        {
            Xrecord rec;
            if (nod.Contains(NodKey))
            {
                rec = (Xrecord)tr.GetObject(nod.GetAt(NodKey), OpenMode.ForWrite);
                rec.Data = new ResultBuffer(new TypedValue((int)DxfCode.Real, scale.Value));
            }
            else
            {
                nod.UpgradeOpen();
                rec = new Xrecord();
                rec.Data = new ResultBuffer(new TypedValue((int)DxfCode.Real, scale.Value));
                ObjectId id = nod.SetAt(NodKey, rec);
                tr.AddNewlyCreatedDBObject(rec, true);
            }
        }
        tr.Commit();
    }

    /// <summary>读 NOD 里用户固定的 scale；没设过返回 null。</summary>
    public static double? ReadUserScale(Database db, Transaction tr)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(NodKey)) return null;
        if (tr.GetObject(nod.GetAt(NodKey), OpenMode.ForRead) is not Xrecord rec) return null;
        foreach (TypedValue tv in rec.Data)
            if (tv.TypeCode == (int)DxfCode.Real && tv.Value is double d)
                return d;
        return null;
    }

    private static void ApplyValues(Database db, Transaction tr, DimStyleTableRecord rec, double scale)
    {
        var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        if (tst.Has("AutoDimText"))
            rec.Dimtxsty = tst["AutoDimText"];
        rec.Dimtxt = 2.5;        // 文字高度(基础)
        rec.Dimasz = 2.5;        // 箭头大小(基础)
        rec.Dimscale = scale;    // 总体比例(自适应或用户固定)
        rec.Dimtad = 1;          // 文字在尺寸线上方(不打断尺寸线)
        rec.Dimtix = true;       // 强制文字在界线内：直径标注的尺寸线才会完整穿过圆心、
                                 // 两端箭头(否则小孔文字外置、尺寸线缩成圆外一段，非国标画法)
        rec.Dimjust = 0;         // 文字沿尺寸线居中
        rec.Dimtmove = 0;        // 文字随尺寸线移动
        rec.Dimtih = true;       // 界线内文字水平(直径 ⌀d 横放，GB)
        rec.Dimtoh = false;      // 界线外文字与尺寸线平行
    }
}
