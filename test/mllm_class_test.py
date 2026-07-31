#!/usr/bin/env python
"""测 3B 视觉模型"有无噪声"的二分类能力（对照计数能力，决定 MLLM 定位）。"""
import base64
import io
import json
import math
import random
import sys
import urllib.request

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

MODEL = "qwen2.5vl:3b"
OLLAMA = "http://127.0.0.1:11434"


def make_image(with_noise, seed=7):
    random.seed(seed)
    fig, ax = plt.subplots(1, 1, figsize=(6, 6), dpi=120)
    ax.set_xlim(0, 100)
    ax.set_ylim(0, 100)
    # 一个清晰的矩形轮廓 + 3 个大圆
    ax.plot([15, 85, 85, 15, 15], [20, 20, 80, 80, 20], color="black", linewidth=1.5)
    for (cx, cy, r) in [(30, 40, 8), (60, 55, 9), (45, 70, 7)]:
        th = [i * math.pi / 180 for i in range(0, 361, 5)]
        ax.plot([cx + r * math.cos(a) for a in th],
                [cy + r * math.sin(a) for a in th], color="black", linewidth=1.2)
    if with_noise:
        for _ in range(40):
            ax.plot([random.uniform(10, 90), random.uniform(10, 90)],
                    [random.uniform(10, 90), random.uniform(10, 90)],
                    color="black", linewidth=0.4)
    ax.set_aspect("equal")
    ax.axis("off")
    buf = io.BytesIO()
    fig.savefig(buf, format="png", dpi=120)
    plt.close(fig)
    return buf.getvalue()


def ask(image_bytes, prompt):
    payload = {
        "model": MODEL,
        "messages": [
            {"role": "user", "content": prompt,
             "images": [base64.b64encode(image_bytes).decode()]}
        ],
        "stream": False,
        "options": {"temperature": 0.0},
    }
    req = urllib.request.Request(
        OLLAMA + "/api/chat", data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.loads(r.read().decode())["message"]["content"]


def main():
    p = "图中是否有很多杂乱的短线段（噪声线）？只回答 有 或 无。"
    for noise in (False, True, False, True):
        img = make_image(noise)
        try:
            print(f"真实={ '有' if noise else '无' } -> 模型: {ask(img, p).strip()[:60]}")
        except Exception as e:
            print("FAIL", e)


if __name__ == "__main__":
    main()
