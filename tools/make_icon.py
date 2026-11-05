"""
Draws the DriveSync logo from the design (design/drivesync.html, symbol "logo") and writes:

    src/DriveSync.App/Assets/DriveSync.ico   the program icon (taskbar, title bar, Explorer, Start menu, tray)
    src/DriveSync.App/Assets/Logo.png        the logo at 256 x 256
    src/DriveSync.App/Assets/Splash.png      the splash screen shown while the app starts

The mark: a rounded teal square (gradient #5ee6d6 to #0f8f86) with a dark folder outline and a
magnifying glass, drawn on the design's 32-unit grid.

Run from the DriveSync folder:   python tools/make_icon.py
Needs Pillow:                    pip install pillow
"""

import io
import struct
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ASSETS = Path(__file__).resolve().parent.parent / "src" / "DriveSync.App" / "Assets"

LIGHT = (0x5e, 0xe6, 0xd6)
DEEP = (0x0f, 0x8f, 0x86)
INK = (0x03, 0x2b, 0x28, 255)
SUPERSAMPLE = 8


def cubic(p0, p1, p2, p3, steps=24):
    pts = []
    for i in range(steps + 1):
        t = i / steps
        mt = 1 - t
        pts.append((
            mt ** 3 * p0[0] + 3 * mt * mt * t * p1[0] + 3 * mt * t * t * p2[0] + t ** 3 * p3[0],
            mt ** 3 * p0[1] + 3 * mt * mt * t * p1[1] + 3 * mt * t * t * p2[1] + t ** 3 * p3[1],
        ))
    return pts


def polyline(draw, pts, width, s):
    pts = [(x * s, y * s) for x, y in pts]
    draw.line(pts, fill=INK, width=round(width * s), joint="curve")
    r = width * s / 2
    for x, y in (pts[0], pts[-1]):
        draw.ellipse((x - r, y - r, x + r, y + r), fill=INK)


def logo(pixels):
    size = pixels * SUPERSAMPLE
    s = size / 32

    # Diagonal gradient, top-left light to bottom-right deep.
    grad = Image.new("RGB", (64, 64))
    px = grad.load()
    for y in range(64):
        for x in range(64):
            t = (x + y) / 126
            px[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(LIGHT, DEEP))
    grad = grad.resize((size, size), Image.BICUBIC).convert("RGBA")

    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((1 * s, 1 * s, 31 * s, 31 * s), radius=8 * s, fill=255)
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    image.paste(grad, (0, 0), mask)

    draw = ImageDraw.Draw(image)
    # Folder: M9 11.5 c0-1.4 1.1-2.5 2.5-2.5 H15 l2 2 h4.5 c1.4 0 2.5 1.1 2.5 2.5 v6
    folder = cubic((9, 11.5), (9, 10.1), (10.1, 9), (11.5, 9))
    folder += [(15, 9), (17, 11), (21.5, 11)]
    folder += cubic((21.5, 11), (22.9, 11), (24, 12.1), (24, 13.5))[1:]
    folder += [(24, 19.5)]
    polyline(draw, folder, 2.2, s)
    # Lens: circle (15.5, 19.5) r 4, stroke 2.2
    r_out, r_in = (4 + 1.1) * s, (4 - 1.1) * s
    cx, cy = 15.5 * s, 19.5 * s
    draw.ellipse((cx - r_out, cy - r_out, cx + r_out, cy + r_out), fill=INK)
    hole = Image.new("L", (size, size), 0)
    ImageDraw.Draw(hole).ellipse((cx - r_in, cy - r_in, cx + r_in, cy + r_in), fill=255)
    image.paste(grad, (0, 0), hole)
    # Handle: (18.5, 22.5) to (22, 26), stroke 2.4
    polyline(draw, [(18.5, 22.5), (22, 26)], 2.4, s)

    return image.resize((pixels, pixels), Image.LANCZOS)


def write_ico(path, images):
    """An .ico holding each size as PNG data (Windows Vista and later)."""
    blobs = []
    for image in images:
        buffer = io.BytesIO()
        image.save(buffer, "PNG")
        blobs.append(buffer.getvalue())
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)
    entries = b""
    for image, blob in zip(images, blobs):
        w, h = image.size
        entries += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    path.write_bytes(header + entries + b"".join(blobs))


def font(names, size):
    for name in names:
        try:
            return ImageFont.truetype(f"C:/Windows/Fonts/{name}", size)
        except OSError:
            continue
    return ImageFont.load_default(size)


def splash():
    """A dark Mica-style card with the logo, the name and the tagline (design tokens: --mica, --text, --text-2)."""
    k = 2
    w, h = 520 * k, 210 * k
    card = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    shadow = Image.new("L", (w, h), 0)
    ImageDraw.Draw(shadow).rounded_rectangle((18 * k, 22 * k, w - 18 * k, h - 10 * k), radius=16 * k, fill=150)
    shadow = shadow.filter(ImageFilter.GaussianBlur(9 * k))
    card.paste(Image.new("RGBA", (w, h), (0, 0, 0, 255)), (0, 0), shadow)
    d = ImageDraw.Draw(card)
    d.rounded_rectangle((12 * k, 12 * k, w - 12 * k, h - 16 * k), radius=16 * k, fill=(22, 28, 34, 255),
                        outline=(255, 255, 255, 34), width=k)
    card.alpha_composite(logo(104 * k), (44 * k, 44 * k))
    title = font(["SegUIVar.ttf", "segoeuisb.ttf", "segoeuib.ttf"], 44 * k)
    tagline = font(["SegUIVar.ttf", "segoeui.ttf"], 16 * k)
    d.text((176 * k, 62 * k), "DriveSync", font=title, fill=(232, 238, 240, 255))
    d.text((178 * k, 120 * k), "Real-time desktop search for Windows", font=tagline, fill=(163, 175, 180, 255))
    return card.resize((w // k, h // k), Image.LANCZOS)


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    write_ico(ASSETS / "DriveSync.ico", [logo(px) for px in sizes])
    logo(256).save(ASSETS / "Logo.png")
    splash().save(ASSETS / "Splash.png")
    print(f"Wrote DriveSync.ico ({', '.join(map(str, sizes))} px), Logo.png and Splash.png to {ASSETS}")


if __name__ == "__main__":
    main()
