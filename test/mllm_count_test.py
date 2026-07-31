#!/usr/bin/env python
"""测试本地视觉模型的"圆孔计数"能力上限（决定 MLLM 分块粒度）。
合成图：n 个圆 + 噪声线段，问模型有几个圆。"""
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


def make_image(n, seed=1):
    random.seed(seed)
    fig, ax = plt.subplots(1, 1, figsize=(6, 6), dpi=120)
    ax.set_xlim(0, 100)
    ax.set_ylim(0, 100)
    for _ in range(n):
        cx, cy = random.uniform(15, 85), random.uniform(15, 85)
        r = random.uniform(8, 12)
        th = [i * math.pi / 180 for i in range(0, 361, 5)]
        ax.plot([cx + r * math.cos(a) for a in th],
                [cy + r * math.sin(a) for a in th], color="black", linewidth=1)
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
            {
                "role": "user",
                "content": prompt,
                "images": [base64.b64encode(image_bytes).decode()],
            }
        ],
        "stream": False,
        "options": {"temperature": 0.0},
    }
    req = urllib.request.Request(
        OLLAMA + "/api/chat",
        data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.loads(r.read().decode())["message"]["content"]


def main():
    counts = [int(x) for x in sys.argv[1:]] or [3, 5, 8, 12, 20]
    prompt = "数一数图中完整圆形（圆环）的数量，只输出一个数字，不要输出其他内容。"
    for n in counts:
        img = make_image(n)
        try:
            reply = ask(img, prompt).strip()
        except Exception as e:
            print(f"n={n}: FAIL {e}")
            continue
        print(f"n={n} -> 模型回答: {reply}")


if __name__ == "__main__":
    main()
