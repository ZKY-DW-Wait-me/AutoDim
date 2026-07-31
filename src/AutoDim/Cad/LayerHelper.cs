using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoDim.Cad;

internal static class LayerHelper
{
    /// <summary>
    /// 确保指定名称的图层存在；不存在则以给定 ACI 颜色创建。返回图层 ObjectId。
    /// </summary>
    public static ObjectId EnsureLayer(Database db, Transaction tr, string name, short colorIndex = 3)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(name))
            return lt[name];

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
