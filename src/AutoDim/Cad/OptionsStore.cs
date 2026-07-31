using Autodesk.AutoCAD.DatabaseServices;

namespace AutoDim.Cad;

/// <summary>
/// 把 AutoDimOptions 的类别开关持久化到 NOD(Xrecord)，跨会话保留。
/// ADIMCFG 命令读写它；其它命令运行时读它决定默认 Categories。
/// </summary>
internal static class OptionsStore
{
    private const string NodKey = "AutoDim_Options";

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
}
