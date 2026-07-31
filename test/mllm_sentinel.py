#!/usr/bin/env python
"""MLLM 清洗质量哨兵：把"清洗前 DXF"与"清洗后 DXF(ADIM_CLEAN 层)"的同一区域渲染图
交给本地视觉模型判断线条密度，输出"原始多/清洗后少=清洗有效"的质量信号。
用法: python test/mllm_sentinel.py <raw.dxf> <clean.dxf> [model]
模型默认 qwen2.5vl:7b（3B 对密度无区分度，勿用）。
"""
import base64
import json
import sys
import urllib.request

import ezdxf
import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

import render_dxf

OLLAMA = "http://127.0.0.1:11434"
MODEL = "qwen2.5vl:7b"


def extents(dxf_path):
    doc = ezdxf.readfile(dxf_path)
    msp = doc.modelspace()
    xs, ys = [], []
    for e in msp:
        if e.dxftype() == "LINE":
            for p in (e.dxf.start, e.dxf.end):
                xs.append(p.x)
                ys.append(p.y)
        elif e.dxftype() == "CIRCLE":
            xs.append(e.dxf.center.x)
            ys.append(e.dxf.center.y)
    return min(xs), min(ys), max(xs), max(ys)


def render_region(dxf_path, layer, x0, y0, x1, y1, out):
    doc = ezdxf.readfile(dxf_path)
    msp = doc.modelspace()
    fig, ax = plt.subplots(1, 1, figsize=(8, 6), dpi=140)
    ax.set_facecolor("white")
    for e in msp:
        if layer is not None and e.dxf.layer != layer:
            continue
        if e.dxftype() == "INSERT":
            render_dxf.draw_insert(ax, e, "black")
        elif e.dxftype() == "DIMENSION":
            for v in e.virtual_entities():
                render_dxf.draw_entity(ax, v, "red")
        else:
            render_dxf.draw_entity(ax, e, "black")
    ax.set_xlim(x0, x1)
    ax.set_ylim(y0, y1)
    ax.set_aspect("equal", adjustable="box")
    ax.axis("off")
    fig.savefig(out, bbox_inches="tight", pad_inches=0.02)
    plt.close(fig)


def ask(path, prompt):
    with open(path, "rb") as f:
        img = base64.b64encode(f.read()).decode()
    payload = {
        "model": MODEL,
        "messages": [{"role": "user", "content": prompt, "images": [img]}],
        "stream": False,
        "options": {"temperature": 0.0},
    }
    req = urllib.request.Request(
        OLLAMA + "/api/chat", data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.loads(r.read().decode())["message"]["content"].strip()


def main():
    raw_dxf, clean_dxf = sys.argv[1], sys.argv[2]
    if len(sys.argv) > 3:
        global MODEL
        MODEL = sys.argv[3]
    x0, y0, x1, y1 = extents(raw_dxf)
    w, h = x1 - x0, y1 - y0
    # 把图纸分成 2 列 × 1 行（宽扁图）的 4 个采样区，各约 1/4 宽
    cols = 2
    regions = []
    for c in range(cols):
        for r in range(2):
            rx0 = x0 + w * c / cols
            rx1 = x0 + w * (c + 1) / cols
            ry0 = y0 + h * r / 2
            ry1 = y0 + h * (r + 1) / 2
            regions.append((f"R{c}{r}", rx0, ry0, rx1, ry1))
    prompt = "图中线条密度如何？用 多 / 中 / 少 回答，只输出一个词。"
    ok = total = 0
    print(f"模型 {MODEL}，采样 {len(regions)} 个区域：")
    for name, rx0, ry0, rx1, ry1 in regions:
        rp, cp = f"test/_s_raw.png", f"test/_s_clean.png"
        render_region(raw_dxf, None, rx0, ry0, rx1, ry1, rp)
        render_region(clean_dxf, "ADIM_CLEAN", rx0, ry0, rx1, ry1, cp)
        rr = ask(rp, prompt)
        cc = ask(cp, prompt)
        good = rr in ("多", "中") and cc == "少"
        ok += good
        total += 1
        print(f"  {name}: 原始={rr} 清洗后={cc} {'OK' if good else '--'}")
    print(f"清洗有效区域: {ok}/{total}")


if __name__ == "__main__":
    main()
