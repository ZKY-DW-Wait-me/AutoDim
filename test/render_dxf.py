#!/usr/bin/env python
"""把 AutoDim 导出的 DXF 渲染成 PNG（供视觉模型/人工检查标注效果）。
用法: python test/render_dxf.py <in.dxf> <out.png> [zoom_x0,y0,x1,y1]
不含文字实体；块引用自动展开。"""
import sys
import math

import ezdxf
import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt


def draw_entity(ax, e, color):
    t = e.dxftype()
    try:
        if t == "LINE":
            ax.plot([e.dxf.start.x, e.dxf.end.x], [e.dxf.start.y, e.dxf.end.y],
                    color=color, linewidth=0.5, solid_capstyle="round")
        elif t == "CIRCLE":
            c, r = e.dxf.center, e.dxf.radius
            th = [i * math.pi / 180 for i in range(0, 361, 4)]
            ax.plot(c.x + r * [math.cos(a) for a in th],
                    c.y + r * [math.sin(a) for a in th], color=color, linewidth=0.5)
        elif t == "ARC":
            c, r = e.dxf.center, e.dxf.radius
            a0, a1 = math.radians(e.dxf.start_angle), math.radians(e.dxf.end_angle)
            if a1 <= a0:
                a1 += 2 * math.pi
            n = max(8, int((a1 - a0) / (math.pi / 90)))
            th = [a0 + (a1 - a0) * i / n for i in range(n + 1)]
            ax.plot(c.x + r * [math.cos(a) for a in th],
                    c.y + r * [math.sin(a) for a in th], color=color, linewidth=0.5)
        elif t == "LWPOLYLINE":
            pts = list(e.get_points())
            xs = [p[0] for p in pts]
            ys = [p[1] for p in pts]
            if e.closed:
                xs.append(xs[0])
                ys.append(ys[0])
            ax.plot(xs, ys, color=color, linewidth=0.5)
        elif t == "POLYLINE":
            verts = list(e.vertices)
            xs = [v.dxf.location.x for v in verts]
            ys = [v.dxf.location.y for v in verts]
            if e.is_closed:
                xs.append(xs[0])
                ys.append(ys[0])
            ax.plot(xs, ys, color=color, linewidth=0.5)
        elif t == "POINT":
            ax.plot([e.dxf.location.x], [e.dxf.location.y], color=color, marker=".",
                    markersize=1, linestyle="none")
        elif t == "TEXT":
            ax.text(e.dxf.insert.x, e.dxf.insert.y, e.dxf.text, fontsize=2.2,
                    color=color, ha="left", va="bottom")
    except Exception:
        pass


def draw_insert(ax, insert, color):
    for v in insert.virtual_entities():
        if v.dxftype() == "INSERT":
            draw_insert(ax, v, color)
        else:
            draw_entity(ax, v, color)


def main():
    src, out = sys.argv[1], sys.argv[2]
    zoom = None
    if len(sys.argv) >= 4:
        zoom = tuple(float(x) for x in sys.argv[3].split(","))
    doc = ezdxf.readfile(src)
    msp = doc.modelspace()
    fig, ax = plt.subplots(1, 1, figsize=(16, 12), dpi=150)
    ax.set_facecolor("white")
    for e in msp:
        if e.dxftype() == "INSERT":
            draw_insert(ax, e, "black")
        elif e.dxftype() == "DIMENSION":
            for v in e.virtual_entities():
                draw_entity(ax, v, "red")
        else:
            draw_entity(ax, e, "black")
    if zoom:
        x0, y0, x1, y1 = zoom
        ax.set_xlim(x0, x1)
        ax.set_ylim(y0, y1)
    else:
        ax.autoscale()
    ax.set_aspect("equal", adjustable="box")
    ax.axis("off")
    fig.savefig(out, bbox_inches="tight", pad_inches=0.05)
    print(f"OK -> {out}")


if __name__ == "__main__":
    main()
