using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoDim.Cad;

internal static class LayerHelper
{
    /// <summary>
    /// 确保指定名称的图层存在，并统一使用给定 ACI 颜色(已存在也更新——
    /// 否则旧图里残留的绿色清洗层不会变)。返回图层 ObjectId。
    /// </summary>
    public static ObjectId EnsureLayer(Database db, Transaction tr, string name, short colorIndex = 3)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(name))
        {
            var existing = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
            existing.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);
            return existing.Id;
        }

        lt.UpgradeOpen();
        var ltr = new LayerTableRecord
        {
            Name = name,
            Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex)
        };
        ObjectId id = lt.Add(ltr);
        tr.AddNewlyCreatedDBObject(ltr, true);
        return id;
    }
}
