#!/usr/bin/env python
"""生成圆弧/圆角通用性测试图（不同尺寸、不同结构），供 run-arc-cases.sh 回归。

覆盖场景：
  a 小零件 50x30：4 个 R5 独立 ARC 圆角 + 2 孔
  b 大板 500x300：4 个 R25 独立 ARC 圆角 + 8 孔阵列
  c U 形槽件：外框 4xR10 + 内部 U 槽（180° 半圆弧独立 ARC）+ 2 孔
  d 混合：Polyline bulge 圆角矩形(4xR10) + 独立 ARC 圆角矩形(4xR6) —— 两种路径都不重复、不遗漏
  e 12 圆角件：外框 4xR8 + 6 个内部小圆角矩形(24xR3) —— 超过 MaxArcDims=8 后同半径合并 N×R
  f 微件 10x8：4 个 R2 —— 极小尺寸比例
  g 大件 2000x1200：4 个 R100 + 6 孔 —— 极大尺寸比例
"""
import os
import sys

import ezdxf

OUT = sys.argv[1] if len(sys.argv) > 1 else "test/arc_cases"
B90 = 0.41421356  # bulge = tan(45°)，90° 圆角


def add_fillet_rect_arcs(msp, x0, y0, x1, y1, r):
    """圆角矩形：4 个角用独立 ARC 实体 + 4 条边用 LINE 实体（SW 常见导出形式）。"""
    msp.add_line((x0 + r, y1), (x1 - r, y1))
    msp.add_line((x1, y1 - r), (x1, y0 + r))
    msp.add_line((x1 - r, y0), (x0 + r, y0))
    msp.add_line((x0, y0 + r), (x0, y1 - r))
    msp.add_arc((x0 + r, y1 - r), r, 180, 270)
    msp.add_arc((x1 - r, y1 - r), r, 270, 360)
    msp.add_arc((x1 - r, y0 + r), r, 0, 90)
    msp.add_arc((x0 + r, y0 + r), r, 90, 180)


def add_fillet_rect_bulge(msp, x0, y0, x1, y1, r):
    """圆角矩形：闭合 LWPOLYLINE + bulge（另一常见导出形式）。"""
    pts = [
        (x0 + r, y0, 0, 0, 0.0), (x1 - r, y0, 0, 0, B90),
        (x1, y0, 0, 0, 0.0), (x1, y1, 0, 0, B90),
        (x1 - r, y1, 0, 0, 0.0), (x0 + r, y1, 0, 0, B90),
        (x0, y1, 0, 0, 0.0), (x0, y0, 0, 0, B90),
    ]
    msp.add_lwpolyline(pts, close=True)


def add_u_slot(msp, cx, cy, h, r):
    """竖直腰形槽：两端 180° 半圆弧 + 左右两条直线（独立实体）。"""
    msp.add_line((cx - r, cy - h / 2 + r), (cx - r, cy + h / 2 - r))
    msp.add_line((cx + r, cy - h / 2 + r), (cx + r, cy + h / 2 - r))
    msp.add_arc((cx, cy + h / 2 - r), r, 180, 360)
    msp.add_arc((cx, cy - h / 2 + r), r, 0, 180)


def case_a(msp):
    add_fillet_rect_arcs(msp, 0, 0, 50, 30, 5)
    msp.add_circle((15, 15), 3)
    msp.add_circle((35, 15), 3)


def case_b(msp):
    add_fillet_rect_arcs(msp, 0, 0, 500, 300, 25)
    for i in range(8):
        msp.add_circle((80 + i * 50, 150), 12)


def case_c(msp):
    add_fillet_rect_arcs(msp, 0, 0, 120, 100, 10)
    add_u_slot(msp, 60, 50, 60, 15)
    msp.add_circle((30, 25), 6)
    msp.add_circle((90, 75), 6)


def case_d(msp):
    add_fillet_rect_bulge(msp, 0, 0, 100, 80, 10)
    add_fillet_rect_arcs(msp, 140, 0, 240, 80, 6)


def case_e(msp):
    add_fillet_rect_arcs(msp, 0, 0, 200, 150, 8)
    for i in range(3):
        add_fillet_rect_arcs(msp, 30 + i * 50, 30, 60 + i * 50, 60, 3)
    for i in range(3):
        add_fillet_rect_arcs(msp, 30 + i * 50, 90, 60 + i * 50, 120, 3)


def case_f(msp):
    add_fillet_rect_arcs(msp, 0, 0, 10, 8, 2)
    msp.add_circle((5, 4), 1.5)


def case_g(msp):
    add_fillet_rect_arcs(msp, 0, 0, 2000, 1200, 100)
    for i in range(6):
        msp.add_circle((300 + i * 300, 600), 80)


CASES = {
    "a_small_50x30_r5": case_a,
    "b_large_500x300_r25": case_b,
    "c_Uslot": case_c,
    "d_mixed_bulge_arc": case_d,
    "e_12fillet": case_e,
    "f_micro_10x8_r2": case_f,
    "g_huge_2000x1200_r100": case_g,
}


def main():
    os.makedirs(OUT, exist_ok=True)
    for name, fn in CASES.items():
        doc = ezdxf.new("R2018")
        fn(doc.modelspace())
        path = os.path.join(OUT, name + ".dxf")
        doc.saveas(path)
        print("OK ->", path)


if __name__ == "__main__":
    main()
