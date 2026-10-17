"""Generate the PetDock application icon set.

Usage:
    python tools/make-icons.py                          # 默认：画本仓库自有的占位图标
    python tools/make-icons.py --placeholder [outdir]   # 同上（显式写法）
    python tools/make-icons.py <source> [outdir]        # 用你自己的图（本机构建；**不要提交**产物）

Default outdir: assets/icons

Produces:
    icon-{16,24,32,48,64,128,256,512}.png   square PNGs, no padding
    icon.ico                                multi-resolution Windows icon
    icon-rounded-{64,256,512}.png           squircle-masked variant for UI use

为什么默认是「画」而不是「读源图」：仓库里原先是 `assets/icons/source/icon.jpg`，它来源不明、
无授权（`assets/icons/README.md` 自己写着 PLACEHOLDER — NOT CLEARED FOR RELEASE），按
docs/52 §5「只发布能力，永不发布别人的内容」，**由它派生的图同样不能进仓库**。因此提交进仓库的
那 12 个图标一律是这个脚本**自己画**的占位图（本项目自有、随内核 Apache-2.0 授权）；所有者自己的
原图留在 `assets/user/icons-source/icon.jpg`（已 gitignore），只在他本机构建时用显式参数传入。
"""

from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parent.parent
SIZES = (16, 24, 32, 48, 64, 128, 256, 512)
ICO_SIZES = [(s, s) for s in (16, 24, 32, 48, 64, 128, 256)]

# 占位图配色取自 docs/31 示例宠物（金图基线中心 RGB 86,132,214），
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


def main() -> int:
    argv = sys.argv[1:]
    force_placeholder = "--placeholder" in argv
    args = [a for a in argv if a != "--placeholder"]

    src = Path(args[0]) if len(args) > 0 else None
    outdir = Path(args[1]) if len(args) > 1 else REPO / "assets" / "icons"

    outdir.mkdir(parents=True, exist_ok=True)

    if not force_placeholder and src is not None and src.is_file():
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
        master.resize((s, s), Image.LANCZOS).save(p, "PNG", optimize=True)
        written.append(p)

    ico = outdir / "icon.ico"
    master.save(ico, "ICO", sizes=ICO_SIZES)
    written.append(ico)

    for s in (64, 256, 512):
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
