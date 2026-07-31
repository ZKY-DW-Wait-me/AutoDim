using Autodesk.AutoCAD.DatabaseServices;

namespace AutoDim.Cad;

/// <summary>
/// 把 AutoDimOptions 的类别开关持久化到 NOD(Xrecord)，跨会话保留。
/// ADIMCFG 命令读写它；其它命令运行时读它决定默认 Categories。
/// </summary>
internal static class OptionsStore
{
    private const string NodKey = "AutoDim_Options";
    private const string AutoCleanKey = "AutoDim_AutoClean";
    private const string CleanKey = "AutoDim_Clean";

    /// <summary>读持久化的类别开关；没设过返回 All(默认全开)。</summary>
    public static int ReadCategories(Database db, Transaction tr)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(NodKey)) return (int)Config.DimCategory.All;
        if (tr.GetObject(nod.GetAt(NodKey), OpenMode.ForRead) is not Xrecord rec) return (int)Config.DimCategory.All;
        foreach (TypedValue tv in rec.Data)
            if (tv.TypeCode == (int)DxfCode.Int32 && tv.Value is int i)
                return i;
        return (int)Config.DimCategory.All;
    }

    /// <summary>写类别开关到 NOD。</summary>
    public static void WriteCategories(Database db, int categories)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (nod.Contains(NodKey))
        {
            var rec = (Xrecord)tr.GetObject(nod.GetAt(NodKey), OpenMode.ForWrite);
            rec.Data = new ResultBuffer(new TypedValue((int)DxfCode.Int32, categories));
        }
        else
        {
            nod.UpgradeOpen();
            var rec = new Xrecord();
            rec.Data = new ResultBuffer(new TypedValue((int)DxfCode.Int32, categories));
            ObjectId id = nod.SetAt(NodKey, rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }
        tr.Commit();
    }

    /// <summary>读"自动清洗"开关（默认关）。</summary>
    public static bool ReadAutoClean(Database db, Transaction tr)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(AutoCleanKey)) return false;
        if (tr.GetObject(nod.GetAt(AutoCleanKey), OpenMode.ForRead) is not Xrecord rec) return false;
        foreach (TypedValue tv in rec.Data)
            if (tv.TypeCode == (int)DxfCode.Int32 && tv.Value is int i)
                return i != 0;
        return false;
    }

    /// <summary>写"自动清洗"开关到 NOD。</summary>
    public static void WriteAutoClean(Database db, bool on)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (nod.Contains(AutoCleanKey))
        {
            var rec = (Xrecord)tr.GetObject(nod.GetAt(AutoCleanKey), OpenMode.ForWrite);
            rec.Data = new ResultBuffer(new TypedValue((int)DxfCode.Int32, on ? 1 : 0));
        }
        else
        {
            nod.UpgradeOpen();
            var rec = new Xrecord();
            rec.Data = new ResultBuffer(new TypedValue((int)DxfCode.Int32, on ? 1 : 0));
            nod.SetAt(AutoCleanKey, rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }
        tr.Commit();
    }

    /// <summary>读清洗参数（SnapTol/MergeTol/MinKeepLength/MinFaceArea/MaxFaceThinness）；没设过返回默认。</summary>
    public static double[] ReadCleanParams(Database db, Transaction tr)
    {
        var def = new[] { 0.5, 1.0, 3.0, 1.0, 60.0 };
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(CleanKey)) return def;
        if (tr.GetObject(nod.GetAt(CleanKey), OpenMode.ForRead) is not Xrecord rec) return def;
        var vals = new double[5];
        int i = 0;
        foreach (TypedValue tv in rec.Data)
            if (tv.TypeCode is >= 40 and <= 44 && tv.Value is double d && i < 5)
                vals[i++] = d;
        return i == 5 ? vals : def;
    }

    /// <summary>写清洗参数到 NOD。</summary>
    public static void WriteCleanParams(Database db, double[] vals)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        var buf = new ResultBuffer();
        for (int i = 0; i < 5; i++)
            buf.Add(new TypedValue(40 + i, vals[i]));
        if (nod.Contains(CleanKey))
        {
            var rec = (Xrecord)tr.GetObject(nod.GetAt(CleanKey), OpenMode.ForWrite);
            rec.Data = buf;
        }
        else
        {
            nod.UpgradeOpen();
            var rec = new Xrecord { Data = buf };
            nod.SetAt(CleanKey, rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }
        tr.Commit();
    }
}
