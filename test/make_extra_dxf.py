#!/usr/bin/env python
"""生成第二张合成测试图 test/extra.dxf（模拟扫描脏图特征）：
- 倾斜弹簧支架（平行四边形 + 2 孔）
- 矩形板 + 6 孔阵列（重复直径，测 N×Ød 合并）
- 圆角矩形（弧 bulge 保留）
- 重复面（同一矩形画两遍、偏移 0.5mm，测近重复抑制）
- 碎片噪声（短随机线段）
- 开放链碎片（不闭合的长段）
"""
import math
import random

import ezdxf

random.seed(42)


def add_rect(msp, x0, y0, x1, y1):
    msp.add_lwpolyline(
        [(x0, y0), (x1, y0), (x1, y1), (x0, y1)], close=True)


def add_fillet_rect(msp, x0, y0, x1, y1, r):
    pts = [(x0 + r, y0), (x1 - r, y0), (x1, y0), (x1, y1), (x1 - r, y1),
           (x0 + r, y1), (x0, y1), (x0, y0)]
    msp.add_lwpolyline(pts, close=True)


def main():
    doc = ezdxf.new("R2018")
    msp = doc.modelspace()

    # 1) 倾斜弹簧支架：平行四边形 + 2 孔
    msp.add_lwpolyline([(0, 0), (80, 30), (60, 90), (-20, 60)], close=True)
    msp.add_circle((20, 40), 6)
    msp.add_circle((45, 58), 6)

    # 2) 矩形板 + 6 孔阵列（Ø8 重复 6 次，测 N×Ød 合并）
    add_rect(msp, 150, 0, 260, 100)
    for i in range(6):
        msp.add_circle((165 + i * 17, 50), 4)
    msp.add_circle((180, 20), 5)   # 独苗孔

    # 3) 圆角矩形（弧）
    add_fillet_rect(msp, 300, 0, 380, 80, 10)

    # 4) 重复面：同一矩形画两遍、偏移 0.5mm（测近重复抑制）
    add_rect(msp, 420, 10, 500, 70)
    add_rect(msp, 420.5, 10.5, 500.5, 70.5)

    # 5) 碎片噪声：短随机线段
    for _ in range(120):
        x = random.uniform(0, 560)
        y = random.uniform(-40, 140)
        dx = random.uniform(-3, 3)
        dy = random.uniform(-3, 3)
        msp.add_line((x, y), (x + dx, y + dy))

    # 6) 开放链碎片：不闭合的长段（模拟外轮廓扫描碎片）
    msp.add_line((560, 0), (600, 0))
    msp.add_line((600, 0), (600, 60))
    msp.add_line((560, 60), (600, 60))

    doc.saveas(r"D:\vscode\project_Autocad-Outline\test\extra.dxf")
    print("OK -> test/extra.dxf")


if __name__ == "__main__":
    main()
