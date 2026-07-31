using Autodesk.AutoCAD.DatabaseServices;

namespace AutoDim.Cad;

internal static class DimStyleHelper
{
    /// <summary>返回当前标注样式（DIMSTYLE 系统变量指向的样式）的 ObjectId。</summary>
    public static ObjectId CurrentDimStyleId(Database db) => db.Dimstyle;
}
