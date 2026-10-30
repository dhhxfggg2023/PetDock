"""Generate the PetDock application icon set.

Usage:
    python tools/make-icons.py                          # 默认：画本仓库自有的占位图标
    python tools/make-icons.py --placeholder [outdir]   # 同上（显式写法）
    python tools/make-icons.py --pixel [outdir]         # 像素猫（所有者 2026-10-26 定稿的形象）
    python tools/make-icons.py <source> [outdir]        # 用你自己的图（本机构建；**不要提交**产物）

Default outdir: assets/icons

Produces:
    icon-{16,24,32,48,64,128,256,512}.png   square PNGs, no padding
    icon.ico                                multi-resolution Windows icon
    icon-rounded-{64,256,512}.png           squircle-masked variant for UI use

为什么默认是「画」而不是「读源图」：仓库里原先是 `assets/icons/source/icon.jpg`，它来源不明、
无授权（`assets/icons/README.md` 自己写着 PLACEHOLDER — NOT CLEARED FOR RELEASE），按
原规格 52 §5「只发布能力，永不发布别人的内容」，**由它派生的图同样不能进仓库**。因此提交进仓库的
那 12 个图标一律是这个脚本**自己画**的图（本项目自有、随内核 Apache-2.0 授权）；所有者自己的
原图留在 `assets/user/icons-source/icon.jpg`（已 gitignore），只在他本机构建时用显式参数传入。

`--pixel` 是 2026-10-26 所有者从若干候选里选定的形象：16×16 网格上手画的像素猫，按整数倍
NEAREST 放大，所以 16/32/48/64/128/256/512 每一档都是清晰的方块，不是模糊的插值图。
"""


from __future__ import annotations

import io
import sys
from pathlib import Path

from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parent.parent
SIZES = (16, 24, 32, 48, 64, 128, 256, 512)
ICO_SIZES = [(s, s) for s in (16, 24, 32, 48, 64, 128, 256)]

# 占位图配色取自 原规格 31 示例宠物（金图基线中心 RGB 86,132,214），
# 让图标与产品形象同一套颜色，而不是随手一个色块。
BG = (86, 132, 214, 255)
BG_DARK = (58, 96, 168, 255)
FACE = (255, 255, 255, 255)
EYE = (32, 44, 66, 255)


def squircle_mask(size: int, radius_ratio: float = 0.22, supersample: int = 4) -> Image.Image:
    """A rounded-rectangle mask; supersampled so small sizes stay smooth."""
    big = size * supersample
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, big - 1, big - 1),
        radius=int(big * radius_ratio),
        fill=255,
    )
    return mask.resize((size, size), Image.LANCZOS)


def draw_placeholder(size: int = 512) -> Image.Image:
    """自绘占位图标：本项目自有的简单宠物脸，可发行、无第三方权利。"""
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    u = size / 512.0

    d.rounded_rectangle((0, 0, size - 1, size - 1), radius=int(size * 0.22), fill=BG)

    # 耳朵：先画，脸盖住根部，只露上半截
    d.polygon([(128 * u, 196 * u), (196 * u, 84 * u), (268 * u, 188 * u)], fill=BG_DARK)
    d.polygon([(244 * u, 188 * u), (316 * u, 84 * u), (384 * u, 196 * u)], fill=BG_DARK)

    d.ellipse((104 * u, 160 * u, 408 * u, 452 * u), fill=FACE)
    d.ellipse((176 * u, 268 * u, 236 * u, 340 * u), fill=EYE)
    d.ellipse((276 * u, 268 * u, 336 * u, 340 * u), fill=EYE)
    d.arc((214 * u, 330 * u, 298 * u, 402 * u), start=20, end=160, fill=EYE, width=max(1, int(12 * u)))

    return im


# 像素猫：16×16 网格，所有者 2026-10-26 从候选中选定（原生小图，按整数倍放大才清晰）。
PIXEL_BASE = 16
PIXEL_BG = (27, 36, 48, 255)
PIXEL_FUR = (251, 243, 228, 255)
PIXEL_INK = (42, 34, 48, 255)
PIXEL_PINK = (238, 150, 168, 255)
PIXEL_SHINE = (255, 255, 255, 255)


def draw_pixel_cat() -> Image.Image:
    """16×16 像素猫：两只尖耳、圆脸、方眼带一块高光、粉鼻。

    只在 16×16 上画一次，之后按整数倍 NEAREST 放大——所以每一档尺寸都是方块，
    不会像插值图那样在 24/48 上糊掉。改形状就改这里，别在 512 上补细节。
    """
    im = Image.new("RGBA", (PIXEL_BASE, PIXEL_BASE), PIXEL_BG)
    d = ImageDraw.Draw(im)
    d.polygon([(3, 6), (4, 1), (7, 5)], fill=PIXEL_FUR)
    d.polygon([(9, 5), (12, 1), (13, 6)], fill=PIXEL_FUR)
    d.ellipse((1, 4, 15, 15), fill=PIXEL_FUR)
    d.rectangle((4, 7, 6, 11), fill=PIXEL_INK)
    d.rectangle((10, 7, 12, 11), fill=PIXEL_INK)
    d.rectangle((5, 8, 6, 9), fill=PIXEL_SHINE)
    d.rectangle((11, 8, 12, 9), fill=PIXEL_SHINE)
    d.rectangle((7, 12, 9, 13), fill=PIXEL_PINK)
    return im


def pixel_scale(master: Image.Image, size: int) -> Image.Image:
    """整数倍放大用 NEAREST（方块清晰），非整数倍（24）才用 LANCZOS。"""
    return master.resize((size, size), Image.NEAREST if size % PIXEL_BASE == 0 else Image.LANCZOS)


def write_ico_png(path: Path, master: Image.Image, sizes) -> None:
    """自己拼 ICO：每一档都给一张 PNG 负载，避免 Pillow 的 thumbnail 插值把 16/32 糊掉。

    ICO 结构：ICONDIR(6B) + N×ICONDIRENTRY(16B) + N×PNG 数据（Vista 起支持 PNG 负载）。
    """
    import struct

    frames, payloads = [], []
    for s in sizes:
        frame = pixel_scale(master, s)
        buf = io.BytesIO()
        frame.save(buf, "PNG", optimize=True)
        payloads.append(buf.getvalue())
        frames.append(s)

    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries = b""
    for s, payload in zip(frames, payloads):
        dim = 0 if s >= 256 else s
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(payload), offset)
        offset += len(payload)
    path.write_bytes(header + entries + b"".join(payloads))



def main() -> int:
    argv = sys.argv[1:]
    pixel = "--pixel" in argv
    force_placeholder = "--placeholder" in argv
    args = [a for a in argv if not a.startswith("--")]

    src = Path(args[0]) if len(args) > 0 else None
    outdir = Path(args[1]) if len(args) > 1 else REPO / "assets" / "icons"

    outdir.mkdir(parents=True, exist_ok=True)

    if pixel:
        master = draw_pixel_cat()
        provenance = "self-authored pixel cat (tools/make-icons.py --pixel, 16x16 grid)"
    elif not force_placeholder and src is not None and src.is_file():
        with Image.open(src) as im:
            im.load()
            print(f"source: {src}  mode={im.mode}  size={im.size}")
            print("WARNING: 这批图标派生自你提供的图，**不要提交**——仓库里应当只有自绘占位图。")
            side = min(im.size)
            left = (im.width - side) // 2
            top = (im.height - side) // 2
            square = im.convert("RGBA").crop((left, top, left + side, top + side))
            master = square.resize((512, 512), Image.LANCZOS)
            provenance = f"derived from {src} (local build — do not commit)"
    else:
        if force_placeholder:
            why = "forced by --placeholder"
        elif src is None:
            why = "no source argument (default: the repo ships only its own placeholder)"
        else:
            why = f"source not found: {src}"
        print(f"source: none ({why}) -> drawing the self-authored placeholder")
        master = draw_placeholder(512)
        provenance = "self-authored placeholder (tools/make-icons.py)"

    written = []
    for s in SIZES:
        p = outdir / f"icon-{s}.png"
        frame = pixel_scale(master, s) if pixel else master.resize((s, s), Image.LANCZOS)
        frame.save(p, "PNG", optimize=True)
        written.append(p)

    ico = outdir / "icon.ico"
    if pixel:
        write_ico_png(ico, master, [s for s, _ in ICO_SIZES])
    else:
        master.save(ico, "ICO", sizes=ICO_SIZES)
    written.append(ico)

    for s in (64, 256, 512):
        if pixel:
            rounded = pixel_scale(master, s).copy()
        else:
            rounded = master.resize((s, s), Image.LANCZOS).copy()
        rounded.putalpha(squircle_mask(s))
        p = outdir / f"icon-rounded-{s}.png"
        rounded.save(p, "PNG", optimize=True)
        written.append(p)


    for p in written:
        print(f"  wrote {p.relative_to(REPO)}  ({p.stat().st_size} bytes)")
    print(f"OK: {len(written)} files in {outdir}  [{provenance}]")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
