#!/usr/bin/env python3
"""把任意一张人像/立绘变成 M0 的桌宠贴图（assets/sample/pet-sample.png）。

两条路：
  * tools/gen-petdock-avatar.py —— 手绘原创形象（发布版的路子）；
  * 本工具 —— 拿现成画作当宠物：**抠掉近白背景 + 按人物轮廓裁切**，
    不再套一个硬圆形（用户 2026-10-15 明确要求：白底透明化、不要严格圆形）。

抠图算法：从画面四边向内做**连通域洪泛**，只把「与边缘连通的近白像素」判为背景
（min(R,G,B) >= --white、max-min <= --spread）。用连通性而不是单纯阈值，
才不会把角色身上同样是白色的衬衫/围裙挖穿。判定后对 alpha 做 0.8px 高斯柔化，
边缘不会有一圈白毛刺。

输出的 128x128 画布按**不透明外接框**等比缩放并居中（--margin 留白），
因此形状就是人物本身的剪影，四角天然透明 —— M0 的「逐像素命中穿透」契约照旧成立。

用法：
  python tools/make-pet-from-image.py --src <图>                       # 默认整图抠图
  python tools/make-pet-from-image.py --src <图> --crop 0.86 --centre 0.5,0.44
  python tools/make-pet-from-image.py --src <图> --circle              # 回退到旧圆形柔边
"""
from __future__ import annotations

import argparse
import os
from collections import deque

from PIL import Image, ImageDraw, ImageFilter

SIZE = 128
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_OUT = os.path.join(REPO, "assets", "sample", "pet-sample.png")
PREVIEW = os.path.join(REPO, "tools", "probes", "_scratch-avatar-preview.png")


def parse_centre(text: str) -> tuple[float, float]:
    try:
        x, y = (float(v) for v in text.split(","))
    except Exception as exc:  # noqa: BLE001
        raise SystemExit(f"--centre 需要 “x,y” 两个 0~1 的小数，实际收到 {text!r}") from exc
    return x, y


def knockout(img: Image.Image, white_min: int, spread_max: int) -> Image.Image:
    """返回 alpha 通道：与边缘连通的近白背景 -> 0，其余 -> 255（边缘 0.8px 柔化）。"""
    w, h = img.size
    px = img.load()
    background = bytearray(w * h)

    def qualifies(x: int, y: int) -> bool:
        r, g, b, a = px[x, y]
        if a == 0:
            return True
        return min(r, g, b) >= white_min and (max(r, g, b) - min(r, g, b)) <= spread_max

    queue: deque[tuple[int, int]] = deque()
    for x in range(w):
        for y in (0, h - 1):
            if not background[y * w + x] and qualifies(x, y):
                background[y * w + x] = 1
                queue.append((x, y))
    for y in range(h):
        for x in (0, w - 1):
            if not background[y * w + x] and qualifies(x, y):
                background[y * w + x] = 1
                queue.append((x, y))

    while queue:
        x, y = queue.popleft()
        for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
            if 0 <= nx < w and 0 <= ny < h and not background[ny * w + nx] and qualifies(nx, ny):
                background[ny * w + nx] = 1
                queue.append((nx, ny))

    alpha = Image.new("L", (w, h), 255)
    ap = alpha.load()
    for y in range(h):
        row = y * w
        for x in range(w):
            if background[row + x]:
                ap[x, y] = 0
    return alpha, 100.0 * sum(background) / (w * h)


def build(src_path: str, centre: tuple[float, float], crop: float, margin: int,
          circle: bool, white_min: int, spread_max: int) -> tuple[Image.Image, float]:
    src = Image.open(src_path).convert("RGBA")
    side = max(16, int(min(src.width, src.height) * crop))
    cx, cy = int(src.width * centre[0]), int(src.height * centre[1])
    left = max(0, min(src.width - side, cx - side // 2))
    top = max(0, min(src.height - side, cy - side // 2))
    cut = src.crop((left, top, left + side, top + side))

    if circle:
        mask = Image.new("L", cut.size, 0)
        ImageDraw.Draw(mask).ellipse((1, 1, cut.size[0] - 2, cut.size[1] - 2), fill=255)
        mask = mask.filter(ImageFilter.GaussianBlur(max(1.0, cut.size[0] / 256.0)))
        removed = 100.0 - 100.0 * sum(1 for v in mask.getdata() if v > 127) / (cut.size[0] * cut.size[1])
    else:
        mask, removed = knockout(cut, white_min, spread_max)
        mask = mask.filter(ImageFilter.GaussianBlur(max(0.8, cut.size[0] / 512.0)))   # ~1px 柔边

    cut.putalpha(mask)
    box = cut.getbbox()
    if box is None:
        raise SystemExit("抠完之后整张图都是透明的——--white/--spread 太宽，或背景本来就不是近白")
    cut = cut.crop(box)

    inner = SIZE - 2 * margin
    scale = min(inner / cut.width, inner / cut.height)
    fitted = cut.resize((max(1, round(cut.width * scale)), max(1, round(cut.height * scale))), Image.LANCZOS)
    out = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    out.paste(fitted, ((SIZE - fitted.width) // 2, (SIZE - fitted.height) // 2))
    return out, removed


def preview(img: Image.Image) -> None:
    """两联预览：左=棋盘格（棋盘格露出来 = 那里 alpha=0，真的透明），右=深色底（查白边/灰边）。"""
    panel = 256
    cell = 16
    big = img.resize((panel, panel), Image.LANCZOS)

    checker = Image.new("RGB", (panel, panel), (255, 255, 255))
    grey = Image.new("RGB", (cell, cell), (198, 202, 210))
    for y in range(0, panel, cell):
        for x in range(0, panel, cell):
            if (x // cell + y // cell) % 2:
                checker.paste(grey, (x, y))

    canvas = Image.new("RGB", (panel * 2, panel), (18, 26, 48))
    canvas.paste(checker, (0, 0))
    canvas.paste(big, (0, 0), big)
    canvas.paste(big, (panel, 0), big)
    canvas.save(PREVIEW, format="PNG")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True)
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--centre", default="0.5,0.5", help="裁切中心，0~1 归一化坐标")
    ap.add_argument("--crop", type=float, default=1.0, help="裁切边长占短边的比例")
    ap.add_argument("--margin", type=int, default=4, help="128 画布四周留白（像素）")
    ap.add_argument("--circle", action="store_true", help="回退到旧的圆形柔边（不抠背景）")
    ap.add_argument("--white", type=int, default=236, help="判为背景的最低通道值")
    ap.add_argument("--spread", type=int, default=16, help="判为背景的最大通道差（饱和度上限）")
    args = ap.parse_args()

    img, removed = build(args.src, parse_centre(args.centre), args.crop, args.margin,
                         args.circle, args.white, args.spread)
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    img.save(args.out, format="PNG", optimize=True)
    preview(img)

    alpha = img.getchannel("A")
    hist = alpha.histogram()
    centre_px = img.getpixel((SIZE // 2, SIZE // 2))
    corners = [img.getpixel(p) for p in ((0, 0), (SIZE - 1, 0), (0, SIZE - 1), (SIZE - 1, SIZE - 1))]
    print(f"OUT {args.out}")
    print(f"PREVIEW {PREVIEW}")
    print(f"SRC {args.src} centre={args.centre} crop={args.crop} circle={args.circle}")
    print(f"KNOCKOUT 背景占比={removed:.1f}% white>={args.white} spread<={args.spread}")
    print(f"ALPHA extrema={alpha.getextrema()} opaque={hist[255]} transparent={hist[0]}")
    print(f"CENTRE rgba={centre_px}")
    print(f"CORNERS {corners}")
    ok = (centre_px[3] == 255 and all(p[3] == 0 for p in corners)
          and hist[255] > 1000 and hist[0] > 1000)
    print("SPRITE-OK" if ok else "SPRITE-BAD")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
