#!/usr/bin/env python3
"""生成 PetDock 的桌宠形象：Q 版「鲸鱼娘」立绘贴图（128x128、32 位带 alpha）。

⚠️ 授权：这是**社区二创形象**的临摹性再创作，只用于用户自己的桌面。
鲸鱼娘的既有特征（蓝色渐变长发、呆毛、鲸鳍耳、蓝眼、大型鲸尾、深蓝+白女仆装）
来自社区共创，主流说法原型是 B 站「上善无形」的原创角色「溟月」，以
CC BY-NC-SA 4.0 开放二创；不得商用。**发布版必须换掉它**（原规格 52 的「零第三方
素材」是 M9 的退出标准），或先取得原作者书面授权。

本脚本自身不复制任何外部像素：所有形状都由下面的几何参数算出，画在空白画布上。

它同时必须满足 M0 的命中穿透契约（这是它作为 assets/sample/pet-sample.png 的职责）：
  - 角色内部完全不透明：鼠标应当被它接住
  - 四角完全透明：鼠标应当穿过去落到桌面

画法（v5）：8 倍超采样（1024）再 LANCZOS 降到 128。
v1 平涂粗黑边、v2/v3 抽象鲸鱼都被否掉了；v4 起换成有明确线稿 + 赛璐璐上色的
Q 版人物——超大头身比（约 2 头身），读得清的是：头饰褶边、鲸鳍耳、长发、蓝眼、白围裙。
v5 修的：呆毛不再是一根黑圈、刘海不再有划痕状线、侧发从蛋形改成尖尾、围裙改成梯形、
鲸尾甩出裙摆右侧。
"""
from __future__ import annotations

import math
import os
from PIL import Image, ImageChops, ImageDraw, ImageFilter

SIZE = 128
SS = 8
S = SIZE * SS

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(REPO, "assets", "sample")
OUT_PATH = os.path.join(OUT_DIR, "pet-sample.png")
PREVIEW_PATH = os.path.join(REPO, "tools", "probes", "_scratch-avatar-preview.png")

# 调色板：参考立绘是「蓝渐变长发 + 深藏青女仆装 + 白围裙」
HAIR_ROOT = (74, 112, 196)      # 发根：偏深的蓝
HAIR_TIP = (168, 210, 248)      # 发梢：浅蓝
HAIR_LINE = (44, 62, 116)       # 头发线稿
SKIN = (255, 233, 222)
SKIN_LINE = (188, 120, 110)
LINE = (38, 44, 78)             # 主线条：深藏青，比纯黑柔和
DRESS = (48, 56, 96)            # 女仆装
DRESS_DARK = (34, 40, 74)
APRON = (250, 250, 253)
APRON_SHADE = (212, 218, 234)
FIN = (52, 66, 116)             # 鲸鳍耳 / 鲸尾：比衣服略深的藏青
FIN_LIGHT = (122, 164, 226)
IRIS_TOP = (34, 66, 150)
IRIS_BOTTOM = (128, 190, 246)
EYE_LINE = (26, 32, 60)
BLUSH = (255, 150, 168)
RIBBON = (86, 132, 216)
MOUTH = (198, 92, 104)


def pt(x: float, y: float) -> tuple[int, int]:
    return (int(round(x * SS)), int(round(y * SS)))


def box(x0: float, y0: float, x1: float, y1: float) -> tuple[int, int, int, int]:
    return (int(x0 * SS), int(y0 * SS), int(x1 * SS), int(y1 * SS))


def gradient(top: tuple[int, int, int], bottom: tuple[int, int, int]) -> Image.Image:
    strip = Image.new("RGB", (1, S))
    for y in range(S):
        t = y / (S - 1)
        strip.putpixel((0, y), tuple(int(round(top[i] + (bottom[i] - top[i]) * t)) for i in range(3)))
    return strip.resize((S, S))


def mask_ellipse(*boxes: tuple[int, int, int, int]) -> Image.Image:
    m = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(m)
    for b in boxes:
        d.ellipse(b, fill=255)
    return m


def mask_poly(points: list[tuple[float, float]]) -> Image.Image:
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).polygon([pt(x, y) for (x, y) in points], fill=255)
    return m


def soft(mask: Image.Image, blur: float, strength: float = 1.0) -> Image.Image:
    m = mask.filter(ImageFilter.GaussianBlur(blur))
    return m.point(lambda v: int(v * strength)) if strength != 1.0 else m


class Canvas:
    """一层一层画；所有坐标都用 128 逻辑空间写。"""

    def __init__(self) -> None:
        self.img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        self.d = ImageDraw.Draw(self.img)

    def fill_mask(self, mask: Image.Image, colour: tuple[int, int, int] | Image.Image) -> None:
        layer = colour if isinstance(colour, Image.Image) else Image.new("RGB", (S, S), colour)
        self.img.paste(layer, (0, 0), mask)

    def ellipse(self, b: tuple[int, int, int, int], fill=None, outline=None, w: float = 1.2) -> None:
        self.d.ellipse(b, fill=fill, outline=outline, width=int(w * SS))

    def poly(self, points: list[tuple[float, float]], fill=None, outline=None, w: float = 1.2) -> None:
        self.d.polygon([pt(x, y) for (x, y) in points], fill=fill, outline=outline, width=int(w * SS))

    def arc(self, b: tuple[int, int, int, int], start: float, end: float, colour, w: float = 1.2) -> None:
        self.d.arc(b, start=start, end=end, fill=colour, width=int(w * SS))


def draw_avatar() -> Image.Image:
    c = Canvas()
    hair = gradient(HAIR_ROOT, HAIR_TIP)

    # ---- 1) 鲸尾：在最底层，从裙摆右侧甩出去 ---------------------------------
    c.poly([(80, 92), (104, 72), (122, 76), (107, 94), (124, 112), (105, 120), (82, 110)],
           fill=FIN, outline=LINE)
    c.poly([(94, 92), (112, 80), (117, 83), (104, 96)], fill=FIN_LIGHT)

    # ---- 2) 后发（一大团轮廓）与鲸鳍耳 ---------------------------------------
    back = mask_ellipse(box(26, 18, 102, 96), box(24, 62, 104, 112))
    c.fill_mask(back, hair)
    c.ellipse(box(26, 18, 102, 96), outline=HAIR_LINE)

    for fin in ([(34, 42), (11, 27), (15, 56), (37, 63)], [(94, 42), (117, 27), (113, 56), (91, 63)]):
        c.poly(fin, fill=FIN, outline=LINE)
        c.poly([(fin[0][0], fin[0][1]), (fin[1][0] * 0.55 + fin[0][0] * 0.45, fin[1][1] * 0.55 + fin[0][1] * 0.45),
                (fin[2][0] * 0.55 + fin[3][0] * 0.45, fin[2][1] * 0.55 + fin[3][1] * 0.45)], fill=FIN_LIGHT)

    # ---- 3) 女仆装：藏青裙身 + 白围裙 + 袖口 ---------------------------------
    c.poly([(48, 84), (80, 84), (88, 102), (84, 119), (44, 119), (40, 102)], fill=DRESS, outline=LINE)
    c.poly([(56, 88), (72, 88), (80, 112), (48, 112)], fill=APRON, outline=LINE, w=1.0)   # 围裙：上窄下宽
    c.poly([(48, 100), (80, 100), (80, 103), (48, 103)], fill=APRON_SHADE)                 # 腰带
    for i in range(6):                                                                     # 下摆褶边
        x = 50 + i * 5.6
        c.ellipse(box(x - 3, 109, x + 3, 115), fill=APRON, outline=APRON_SHADE, w=0.8)
    for side in (-1, 1):                                                                   # 袖 + 白袖口
        cx = 64 + side * 25
        c.ellipse(box(cx - 9, 87, cx + 9, 106), fill=DRESS, outline=LINE)
        c.ellipse(box(cx - 9, 97, cx + 9, 106), fill=APRON, outline=LINE, w=1.0)
    c.poly([(58, 85), (64, 90), (70, 85), (64, 82)], fill=DRESS_DARK, outline=LINE, w=1.0)  # 领结
    c.poly([(60, 87), (68, 87), (70, 94), (58, 94)], fill=DRESS_DARK, outline=LINE, w=1.0)

    # ---- 4) 前发：两侧长发（尖尾）+ 头顶刘海 --------------------------------
    for lock in ([(25, 50), (41, 43), (43, 84), (33, 110), (24, 104), (29, 82)],
                 [(103, 50), (87, 43), (85, 84), (95, 110), (104, 104), (99, 82)]):
        c.poly(lock, fill=None, outline=None)                     # 仅为占位：形状用遮罩填渐变
        c.fill_mask(mask_poly(lock), hair)
        c.poly(lock, fill=None, outline=HAIR_LINE, w=1.1)
        c.poly([(lock[0][0] + 3, lock[0][1] + 6), (lock[1][0] - 3, lock[1][1] + 6),
                (lock[1][0] - 4, lock[1][1] + 26), (lock[0][0] + 4, lock[0][1] + 26)], fill=HAIR_TIP)

    # ---- 5) 脸 ---------------------------------------------------------------
    c.ellipse(box(37, 26, 91, 86), fill=SKIN, outline=SKIN_LINE, w=0.8)

    bangs = ImageChops.multiply(mask_ellipse(box(31, 18, 97, 62)),
                                mask_poly([(28, 20), (100, 20), (100, 42), (28, 42)]))
    c.fill_mask(bangs, hair)
    for (x, y) in ((40, 52), (52, 50), (64, 53), (76, 50), (88, 52)):      # 发梢尖角（填色，不描线）
        c.poly([(x - 6, 40), (x, y), (x + 6, 40)], fill=HAIR_ROOT)
    c.ellipse(box(31, 18, 97, 62), outline=None)

    # ---- 6) 女仆头饰：一排褶边 + 蓝蝴蝶结 + 呆毛 -----------------------------
    for i in range(9):
        a = math.radians(204 + i * 16.5)
        fx, fy = 64 + 33 * math.cos(a), 54 + 33 * math.sin(a)
        c.ellipse(box(fx - 5, fy - 5, fx + 5, fy + 5), fill=APRON, outline=APRON_SHADE, w=0.9)
    c.arc(box(31, 20, 97, 88), 200, 340, APRON_SHADE, 2.0)
    c.poly([(30, 24), (22, 19), (24, 32)], fill=RIBBON, outline=LINE, w=1.0)
    c.poly([(36, 24), (44, 19), (42, 32)], fill=RIBBON, outline=LINE, w=1.0)
    c.ellipse(box(30, 22, 36, 30), fill=RIBBON, outline=LINE, w=1.0)
    c.arc(box(64, 8, 86, 26), 185, 345, HAIR_ROOT, 1.6)                    # 呆毛：细、短、蓝色

    # ---- 7) 眼睛：虹膜渐变 + 厚重的上眼睑 + 两点高光 --------------------------
    for cx in (51, 77):
        c.ellipse(box(cx - 8.5, 51, cx + 8.5, 70), fill=(255, 255, 255), outline=EYE_LINE, w=1.0)
        c.fill_mask(mask_ellipse(box(cx - 7.5, 52, cx + 7.5, 69)), gradient(IRIS_TOP, IRIS_BOTTOM))
        c.d.pieslice(box(cx - 8.5, 48, cx + 8.5, 68), start=180, end=360, fill=EYE_LINE)
        c.ellipse(box(cx - 5.5, 53, cx - 0.5, 59), fill=(255, 255, 255))
        c.ellipse(box(cx + 1.5, 63, cx + 4.5, 66.5), fill=(255, 255, 255))
        c.arc(box(cx - 8.5, 51, cx + 8.5, 70), 0, 180, EYE_LINE, 1.0)

    for cx in (35, 93):                                                    # 腮红
        c.fill_mask(soft(mask_ellipse(box(cx - 7, 66, cx + 7, 76)), SS * 1.6, 0.45), BLUSH)

    # ---- 8) 嘴：一个小张开的笑 ------------------------------------------------
    c.d.pieslice(box(59, 74, 69, 82), start=0, end=180, fill=MOUTH, outline=LINE, width=int(1.0 * SS))

    return c.img


def preview(big: Image.Image) -> None:
    panel = big.width // 2
    small = big.resize((panel, panel), Image.LANCZOS)
    out = Image.new("RGB", (panel * 2, panel), (18, 26, 48))
    for i, bg in enumerate(((214, 216, 220), (18, 26, 48))):
        out.paste(bg, (i * panel, 0, (i + 1) * panel, panel))
        out.paste(small, (i * panel, 0), small)
    out.save(PREVIEW_PATH, format="PNG")


def main() -> int:
    big = draw_avatar()
    img = big.resize((SIZE, SIZE), Image.LANCZOS)

    os.makedirs(OUT_DIR, exist_ok=True)
    img.save(OUT_PATH, format="PNG", optimize=True)
    preview(big)

    alpha = img.getchannel("A")
    lo, hi = alpha.getextrema()
    hist = alpha.histogram()
    opaque, transparent = hist[255], hist[0]
    centre = img.getpixel((SIZE // 2, SIZE // 2))
    corners = [img.getpixel(p) for p in ((0, 0), (SIZE - 1, 0), (0, SIZE - 1), (SIZE - 1, SIZE - 1))]

    print(f"OUT {OUT_PATH}")
    print(f"PREVIEW {PREVIEW_PATH}")
    print(f"SIZE {img.size[0]}x{img.size[1]} mode={img.mode}")
    print(f"ALPHA extrema=({lo},{hi}) opaque={opaque} transparent={transparent}")
    print(f"CENTRE rgba={centre}")
    print(f"CORNERS {corners}")

    ok = (
        lo == 0 and hi == 255 and opaque > 1000 and transparent > 1000
        and centre[3] == 255
        and all(px[3] == 0 for px in corners)
    )
    print("SPRITE-OK" if ok else "SPRITE-BAD")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
