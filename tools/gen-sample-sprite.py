#!/usr/bin/env python3
"""程序化生成 M0 的占位示例宠物贴图（assets/sample/pet-sample.png）。

许可：本脚本与它产出的图都是 PetDock 自有资产，Apache-2.0。没有任何第三方素材参与，
目的是让 docs/52 的「零第三方素材」从第一天起就是真的，而不是事后清理。

画什么：一个 128x128、32 位带 alpha 的软圆团 + 两只眼睛。刻意画得朴素——
它只是一个用来验证「逐像素命中穿透」的靶子：圆形内部不透明（应当接住鼠标），
四角完全透明（应当穿透到桌面）。
"""
from __future__ import annotations

import os
from PIL import Image, ImageDraw

SIZE = 128
OUT_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "assets", "sample")
OUT_PATH = os.path.join(OUT_DIR, "pet-sample.png")

BODY = (86, 132, 214, 255)      # 不透明：命中测试应当命中
SHADE = (54, 92, 168, 255)      # 底部阴影，同一 alpha
EYE = (24, 28, 40, 255)
GLINT = (255, 255, 255, 220)


def draw_pet() -> Image.Image:
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # 身体：外接正方形内切椭圆，留出 8px 边距
    box = (10, 8, SIZE - 10, SIZE - 4)
    d.ellipse(box, fill=BODY)

    # 底部阴影弧，让形状在视觉上有上下之分（不影响命中：同样不透明）
    d.pieslice((12, SIZE - 46, SIZE - 12, SIZE - 2), start=20, end=160, fill=SHADE)

    # 眼睛
    eye_y = 52
    for cx in (46, 82):
        d.ellipse((cx - 9, eye_y - 11, cx + 9, eye_y + 11), fill=EYE)
        d.ellipse((cx - 4, eye_y - 6, cx + 2, eye_y), fill=GLINT)

    return img


def main() -> int:
    img = draw_pet()
    os.makedirs(OUT_DIR, exist_ok=True)
    img.save(OUT_PATH, format="PNG", optimize=True)

    # 自检：alpha 直方图必须同时存在 0 与 255，否则「穿透 vs 命中」无从验证
    alpha = img.getchannel("A")
    lo, hi = alpha.getextrema()
    opaque = sum(1 for a in alpha.getdata() if a == 255)
    transparent = sum(1 for a in alpha.getdata() if a == 0)
    print(f"OUT {OUT_PATH}")
    print(f"SIZE {img.size[0]}x{img.size[1]} mode={img.mode}")
    print(f"ALPHA extrema=({lo},{hi}) opaque={opaque} transparent={transparent}")
    ok = lo == 0 and hi == 255 and opaque > 1000 and transparent > 1000
    print("SPRITE-OK" if ok else "SPRITE-BAD")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
