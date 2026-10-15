"""Generate the PetDock application icon set from a single square source image.

Usage:
    python tools/make-icons.py [source] [outdir]

Default source: icon.jpg at the repository root.
Default outdir:  assets/icons

Produces:
    icon-{16,24,32,48,64,128,256,512}.png   square PNGs, no padding
    icon.ico                                multi-resolution Windows icon
    icon-rounded-{64,256,512}.png           squircle-masked variant for UI use
"""

from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parent.parent
SIZES = (16, 24, 32, 48, 64, 128, 256, 512)
ICO_SIZES = [(s, s) for s in (16, 24, 32, 48, 64, 128, 256)]


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


def main() -> int:
    src = Path(sys.argv[1]) if len(sys.argv) > 1 else REPO / "icon.jpg"
    outdir = Path(sys.argv[2]) if len(sys.argv) > 2 else REPO / "assets" / "icons"

    if not src.is_file():
        print(f"ERROR: source image not found: {src}")
        return 2

    outdir.mkdir(parents=True, exist_ok=True)
    with Image.open(src) as im:
        im.load()
        print(f"source: {src}  mode={im.mode}  size={im.size}")

        # Square it by centre-cropping, then keep RGBA from here on.
        side = min(im.size)
        left = (im.width - side) // 2
        top = (im.height - side) // 2
        square = im.convert("RGBA").crop((left, top, left + side, top + side))

        master = square.resize((512, 512), Image.LANCZOS)

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
    print(f"OK: {len(written)} files in {outdir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
