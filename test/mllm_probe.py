#!/usr/bin/env python
"""MLLM 语义层可行性探针：用 ollama 里的本地视觉模型识别机械图纸渲染图。
用法: python test/mllm_probe.py <image.png> [server]
输出：模型对图纸的语义分类 JSON（轮廓/孔/中心线/填充噪声/标注）。
"""
import base64
import json
import sys
import urllib.request

MODEL = "qwen2.5vl:3b"
PROMPT = (
    "这是一张机械工程图纸的线框图（CAD 图纸）。请仔细观察，把图中的图形元素分类，"
    "只输出一个 JSON 对象，不要输出其他内容，格式如下：\n"
    '{"轮廓": "描述实体轮廓线及其大致位置", '
    '"孔_圆": "描述圆形特征及其位置和数量", '
    '"中心线": "描述点划线中心线", '
    '"填充噪声": "描述剖面线/填充/杂乱短线段等噪声", '
    '"尺寸标注": "描述标注文字/尺寸线的位置和数值"}'
)


def ask(server: str, image_path: str) -> dict:
    with open(image_path, "rb") as f:
        img_b64 = base64.b64encode(f.read()).decode()
    payload = {
        "model": MODEL,
        "messages": [
            {
                "role": "user",
                "content": PROMPT,
                "images": [img_b64],
            }
        ],
        "stream": False,
        "options": {"temperature": 0.1},
    }
    req = urllib.request.Request(
        server + "/api/chat",
        data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.loads(r.read().decode())


def main():
    image = sys.argv[1]
    server = sys.argv[2] if len(sys.argv) > 2 else "http://127.0.0.1:11434"
    print(f"server={server} model={MODEL} image={image}")
    try:
        resp = ask(server, image)
    except Exception as e:
        print(f"FAIL: 调用 ollama 失败: {e}")
        print("提示：确认 ollama 已安装且模型已 pull（ollama list）")
        sys.exit(1)
    text = resp.get("message", {}).get("content", "")
    print("---- 模型回复 ----")
    print(text)
    print("------------------")


if __name__ == "__main__":
    main()
