using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace AutoDim.Selection;

/// <summary>触发/取集方式。</summary>
public enum TriggerMode
{
    Pickfirst,  // 命令前已选中的对象（pickfirst 集）
    Selection,  // 交互式选择对象
    All,        // 整张图（模型空间）符合过滤条件的对象
    Window      // 框选窗口内对象
}

internal static class SelectionService
{
    // 只关心轮廓/孔相关实体：多段线、直线、圆弧、圆。逗号分隔即“任一类型”。
    private static readonly SelectionFilter EntityFilter = new(new[]
    {
        new TypedValue((int)DxfCode.Start, "LWPOLYLINE,LINE,ARC,CIRCLE")
    });

    /// <summary>按模式取得对象集合。返回 null 表示取消或空集。</summary>
    public static ObjectId[]? Acquire(Editor ed, TriggerMode mode)
    {
        switch (mode)
        {
            case TriggerMode.Pickfirst:
                return Extract(ed.SelectImplied());

            case TriggerMode.Selection:
                return Extract(ed.GetSelection(new PromptSelectionOptions(), EntityFilter));

            case TriggerMode.All:
                return Extract(ed.SelectAll(EntityFilter));

            case TriggerMode.Window:
            {
                var p1 = ed.GetPoint("\n指定窗口第一个角点: ");
                if (p1.Status != PromptStatus.OK) return null;
                var p2 = ed.GetCorner("\n指定对角点: ", p1.Value);
                if (p2.Status != PromptStatus.OK) return null;
                return Extract(ed.SelectCrossingWindow(p1.Value, p2.Value, EntityFilter));
            }

            default:
                return null;
        }
    }

    private static ObjectId[]? Extract(PromptSelectionResult r)
        => (r.Status == PromptStatus.OK && r.Value != null && r.Value.Count > 0)
            ? r.Value.GetObjectIds()
            : null;
}
