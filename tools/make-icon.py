"""
Generate the RoboCopyGUI app icon:
  Assets/AppIcon.ico (multi-size: 16/24/32/48/64/128/256)
  and PNG previews under tools/preview/ so a human can eyeball quality.

Design: rounded blue tile with a white folder + yellow "transfer arrow".
At small sizes (<=24px) the folder is dropped in favor of just the arrow
on the tile so the symbol still reads at 16x16.

Run from the repo root:
    python tools/make-icon.py
"""

from __future__ import annotations
import io
import struct
from pathlib import Path
from PIL import Image, ImageDraw

# ---- design tokens ----
BG_FROM = (15, 108, 189)      # #0F6CBD  Fluent accent blue
BG_TO   = (0,  90, 158)       # #005A9E  slight gradient bottom
FG      = (255, 255, 255)     # white folder
ARROW   = (255, 220, 60)      # warm yellow transfer arrow
ARROW_OUTLINE = (0, 60, 110, 255)
SHADOW  = (0, 0, 0, 60)

REPO = Path(__file__).resolve().parent.parent
ASSETS = REPO / "Assets"
PREVIEW = REPO / "tools" / "preview"
PREVIEW.mkdir(parents=True, exist_ok=True)


def vgrad(size: int, top: tuple[int, int, int], bot: tuple[int, int, int]) -> Image.Image:
    img = Image.new("RGB", (1, size))
    for y in range(size):
        t = y / max(1, size - 1)
        r = int(top[0] * (1 - t) + bot[0] * t)
        g = int(top[1] * (1 - t) + bot[1] * t)
        b = int(top[2] * (1 - t) + bot[2] * t)
        img.putpixel((0, y), (r, g, b))
    return img.resize((size, size))


def rounded_mask(size: int, radius: int) -> Image.Image:
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, size - 1, size - 1), radius=radius, fill=255)
    return m


def _draw_arrow(canvas: Image.Image, size: int,
                shaft_left: int, mid_y: int,
                shaft_thick: int, head_back: int, head_tip: int, head_half: int,
                fill=ARROW, draw_outline=True) -> Image.Image:
    """Paint a right-pointing arrow onto canvas (composed via alpha)."""
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    d.rectangle(
        (shaft_left, mid_y - shaft_thick // 2, head_back, mid_y + shaft_thick // 2),
        fill=fill)
    d.polygon(
        [(head_back, mid_y - head_half),
         (head_tip,  mid_y),
         (head_back, mid_y + head_half)],
        fill=fill)
    if draw_outline and size >= 48:
        ow = max(1, size // 128)
        d.rectangle(
            (shaft_left, mid_y - shaft_thick // 2, head_back, mid_y + shaft_thick // 2),
            outline=ARROW_OUTLINE, width=ow)
        d.polygon(
            [(head_back, mid_y - head_half),
             (head_tip,  mid_y),
             (head_back, mid_y + head_half)],
            outline=ARROW_OUTLINE, width=ow)
    return Image.alpha_composite(canvas, layer)


def render_large(size: int) -> Image.Image:
    """
    >=32px: full scene — white folder with a yellow arrow piercing left-to-right.
    Tuned so even 32px reads as "folder + arrow", not just "arrow".
    """
    radius = max(2, size // 6)
    bg = vgrad(size, BG_FROM, BG_TO)
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.paste(bg, (0, 0), rounded_mask(size, radius))
    d = ImageDraw.Draw(canvas)

    pad      = max(2, size // 8)
    tab_h    = max(2, size // 8)
    tab_w    = max(4, size // 3)
    body_top = pad + tab_h
    body_bot = size - pad - max(2, size // 12)
    folder_left  = pad
    folder_right = size - pad

    if size >= 48:
        sh = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        sd = ImageDraw.Draw(sh)
        off = max(1, size // 64)
        sd.rounded_rectangle(
            (folder_left + off, body_top + off, folder_right + off, body_bot + off),
            radius=max(1, size // 24), fill=SHADOW)
        canvas = Image.alpha_composite(canvas, sh)
        d = ImageDraw.Draw(canvas)

    # Folder tab + body
    d.rounded_rectangle(
        (folder_left, pad, folder_left + tab_w, body_top + max(1, size // 32)),
        radius=max(1, size // 24), fill=FG)
    d.rounded_rectangle(
        (folder_left, body_top, folder_right, body_bot),
        radius=max(1, size // 24), fill=FG)

    # Arrow stays INSIDE the folder body — keeps the folder identity readable
    # at 32px, while a bold yellow stripe still signals "transfer / move".
    mid_y = (body_top + body_bot) // 2 + max(1, size // 32)
    shaft_thick = max(3, size // 8)
    margin = max(2, size // 12)
    shaft_left  = folder_left + margin
    head_tip    = folder_right - margin
    head_back   = head_tip - max(4, size // 5)
    head_half   = max(3, size // 6)

    return _draw_arrow(
        canvas, size,
        shaft_left, mid_y,
        shaft_thick, head_back, head_tip, head_half)


def render_small(size: int) -> Image.Image:
    """
    <=24px: drop the folder, render just a bold yellow arrow on the blue tile.
    At 16x16 there isn't enough room for two layered shapes to read clearly,
    so we keep the silhouette unambiguous: blue tile + right-arrow = "transfer".
    """
    radius = max(2, size // 5)
    bg = vgrad(size, BG_FROM, BG_TO)
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.paste(bg, (0, 0), rounded_mask(size, radius))

    margin = max(1, size // 8)
    shaft_thick = max(2, size // 4)
    mid_y = size // 2
    shaft_left = margin
    head_tip = size - margin
    head_back = head_tip - max(3, size // 3)
    head_half = max(3, size // 3)

    return _draw_arrow(
        canvas, size,
        shaft_left, mid_y,
        shaft_thick, head_back, head_tip, head_half,
        draw_outline=False)


def render_icon(size: int) -> Image.Image:
    return render_small(size) if size <= 24 else render_large(size)


def write_ico_manual(out_path: Path, images: list[Image.Image]) -> None:
    """
    Build a proper multi-resolution .ico. Pillow's ICO writer with `sizes=`
    downsamples a single base image (losing the per-size tuning), and the
    `append_images=` form only writes one entry. The format is simple enough
    to assemble directly: an ICONDIR header + one ICONDIRENTRY per image,
    each pointing at a PNG-encoded payload.
    """
    images = sorted(images, key=lambda im: im.size[0])
    # PNG payloads (each image already has its own RGBA bits).
    payloads: list[bytes] = []
    for img in images:
        buf = io.BytesIO()
        img.save(buf, format="PNG", optimize=True)
        payloads.append(buf.getvalue())

    n = len(images)
    header = struct.pack("<HHH", 0, 1, n)  # reserved=0, type=1 (ICO), count=n
    entry_size = 16
    data_offset = 6 + entry_size * n

    entries = bytearray()
    for img, payload in zip(images, payloads):
        w, h = img.size
        entries += struct.pack(
            "<BBBBHHII",
            0 if w == 256 else w,   # width  (0 means 256)
            0 if h == 256 else h,   # height (0 means 256)
            0,                       # color palette
            0,                       # reserved
            1,                       # color planes
            32,                      # bits per pixel
            len(payload),
            data_offset,
        )
        data_offset += len(payload)

    with open(out_path, "wb") as f:
        f.write(header)
        f.write(bytes(entries))
        for payload in payloads:
            f.write(payload)


def main() -> None:
    sizes = [16, 24, 32, 48, 64, 128, 256]
    pngs: list[Image.Image] = []
    for s in sizes:
        img = render_icon(s)
        pngs.append(img)
        img.save(PREVIEW / f"appicon-{s}.png")
        print(f"  wrote preview/appicon-{s}.png")

    ico_path = ASSETS / "AppIcon.ico"
    ASSETS.mkdir(exist_ok=True)
    write_ico_manual(ico_path, pngs)
    print(f"\nWrote {ico_path}  ({ico_path.stat().st_size} bytes, "
          f"{len(sizes)} resolutions: {sizes})")


if __name__ == "__main__":
    main()

