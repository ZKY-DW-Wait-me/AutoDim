#!/usr/bin/env python
"""MLLM 质量验证：对比本地视觉模型对机械图纸的
  1) 圆孔计数  2) 线条密度(多/中/少)  3) 噪声有无  的判断能力。
用法: python test/mllm_quality_test.py [model]
模型默认 qwen2.5vl:3b；对比时可传 qwen2.5vl:7b。
"""
import base64
import json
import sys
import urllib.request

OLLAMA = "http://127.0.0.1:11434"


def ask(model: str, image_path: str, prompt: str) -> str:
    with open(image_path, "rb") as f:
        img = base64.b64encode(f.read()).decode()
    payload = {
        "model": model,
        "messages": [
            {"role": "user", "content": prompt, "images": [img]}
        ],
        "stream": False,
        "options": {"temperature": 0.0},
    }
    req = urllib.request.Request(
        OLLAMA + "/api/chat",
        data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read().decode())["message"]["content"].strip()


def main():
    model = sys.argv[1] if len(sys.argv) > 1 else "qwen2.5vl:3b"
    print(f"===== 模型 {model} =====")

    # 1) 计数：合成图（真实数量 5 / 12）
    from mllm_count_test import make_image
    import tempfile, os
    tmp = tempfile.mkdtemp()
    for n in (5, 12):
        p = os.path.join(tmp, f"c{n}.png")
        with open(p, "wb") as f:
            f.write(make_image(n))
        r = ask(model, p, "数一数图中完整圆形（圆环）的数量，只输出一个数字。")
        print(f"计数 n={n}: 模型={r!r}")

    # 2) 密度：真实图纸清洗前/后（2.32% vs 0.37% 墨迹）
    for name, path in (("原始", "test/raw_zoom.png"), ("清洗后", "test/clean_zoom.png")):
        r = ask(model, path, "图中线条密度如何？用 多 / 中 / 少 回答，只输出一个词。")
        print(f"密度 {name}: 模型={r!r}")

    # 3) 噪声有无（合成图 4 轮）
    from mllm_class_test import make_image
    for noise in (False, True, False, True):
        p = os.path.join(tmp, f"n{int(noise)}.png")
        with open(p, "wb") as f:
            f.write(make_image(noise))
        r = ask(model, p, "图中是否有很多杂乱的短线段（噪声线）？只回答 有 或 无。")
        print(f"噪声 真实={'有' if noise else '无'}: 模型={r!r}")


if __name__ == "__main__":
    main()
