using Autodesk.AutoCAD.DatabaseServices;

namespace AutoDim.Cad;

/// <summary>
/// 给自动生成的标注/中心线实体打一个 ADIM XData 标记(RegAppName="AUTODIM")，
/// 这样重跑时可以"只删自己生成的"，而不是按图层+区域无差别清场——
/// 用户手标在同一图层的标注不会被擦，跨区域同图层的也只清本区域带标记的。
/// </summary>
internal static class AdimMarker
{
    public const string AppName = "AUTODIM";

    /// <summary>确保 RegApp "AUTODIM" 已注册。返回其 ObjectId。</summary>
    public static ObjectId EnsureApp(Database db, Transaction tr)
    {
        var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (rat.Has(AppName)) return rat[AppName];
        rat.UpgradeOpen();
        var rec = new RegAppTableRecord { Name = AppName };
        ObjectId id = rat.Add(rec);
        tr.AddNewlyCreatedDBObject(rec, true);
        return id;
    }

    /// <summary>给实体打 AUTODIM 标记(可多次调用，XData 只存一份)。</summary>
    public static void Mark(Database db, Transaction tr, Entity ent)
    {
        EnsureApp(db, tr);
        var rb = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                                   new TypedValue((int)DxfCode.ExtendedDataAsciiString, "1"));
        ent.XData = rb;
    }

    /// <summary>实体是否带 AUTODIM XData 标记。</summary>
    public static bool IsMarked(Entity ent)
    {
        using ResultBuffer? rb = ent.GetXDataForApplication(AppName);
        return rb != null;
    }
}
