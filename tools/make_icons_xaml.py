"""
Turns the SVG icon symbols in design/drivesync.html into WPF geometries:
src/DriveSync.App/Themes/Icons.xaml.

Every <path>, <circle>, <rect> and <ellipse> of a symbol becomes one figure of a single geometry,
written with explicit absolute commands (M, L, C, Q, A, Z) so WPF's path parser reads it exactly
as a browser would.

Run from the DriveSync folder:   python tools/make_icons_xaml.py
"""

import math
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
HTML = ROOT / "design" / "drivesync.html"
OUT = ROOT / "src" / "DriveSync.App" / "Themes" / "Icons.xaml"

NUM = re.compile(r"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")
TOKEN = re.compile(r"[MmLlHhVvCcSsQqTtAaZz]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")


def fmt(v):
    s = f"{v:.3f}".rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


def tokens_with_flags(d):
    """Tokenizes path data. Arc flags can be written without separators ("a1 1 0 011 1"), so arcs read flags digit by digit."""
    out = []
    i = 0
    cmd = None
    arg_index = 0
    while i < len(d):
        c = d[i]
        if c in " ,\t\n\r":
            i += 1
            continue
        if c.isalpha():
            cmd = c
            out.append(c)
            arg_index = 0
            i += 1
            continue
        if cmd in "Aa" and arg_index % 7 in (3, 4) and c in "01":
            out.append(c)
            arg_index += 1
            i += 1
            continue
        m = NUM.match(d, i)
        if not m:
            raise ValueError(f"bad path data at {i}: {d[i:i+20]}")
        out.append(m.group(0))
        arg_index += 1
        i = m.end()
    return out


def path_to_abs(d):
    toks = tokens_with_flags(d)
    out = []
    i = 0
    x = y = 0.0
    sx = sy = 0.0
    last_ctrl = None
    last_cmd = None
    cmd = None

    def nums(n):
        nonlocal i
        vals = [float(toks[i + k]) for k in range(n)]
        i += n
        return vals

    while i < len(toks):
        t = toks[i]
        if t.isalpha():
            cmd = t
            i += 1
            if cmd in "Zz":
                out.append("Z")
                x, y = sx, sy
                last_ctrl = None
                last_cmd = "Z"
                continue
        elif cmd is None:
            raise ValueError("path data must start with a command")
        rel = cmd.islower()
        C = cmd.upper()
        if C == "M":
            nx, ny = nums(2)
            if rel:
                nx += x; ny += y
            x, y = nx, ny
            sx, sy = x, y
            out.append(f"M{fmt(x)},{fmt(y)}")
            cmd = "l" if rel else "L"   # following pairs are line-tos
            last_ctrl = None
        elif C == "L":
            nx, ny = nums(2)
            if rel:
                nx += x; ny += y
            x, y = nx, ny
            out.append(f"L{fmt(x)},{fmt(y)}")
            last_ctrl = None
        elif C == "H":
            (nx,) = nums(1)
            x = nx + x if rel else nx
            out.append(f"L{fmt(x)},{fmt(y)}")
            last_ctrl = None
        elif C == "V":
            (ny,) = nums(1)
            y = ny + y if rel else ny
            out.append(f"L{fmt(x)},{fmt(y)}")
            last_ctrl = None
        elif C == "C":
            x1, y1, x2, y2, nx, ny = nums(6)
            if rel:
                x1 += x; y1 += y; x2 += x; y2 += y; nx += x; ny += y
            out.append(f"C{fmt(x1)},{fmt(y1)} {fmt(x2)},{fmt(y2)} {fmt(nx)},{fmt(ny)}")
            last_ctrl = (x2, y2)
            x, y = nx, ny
        elif C == "S":
            x2, y2, nx, ny = nums(4)
            if rel:
                x2 += x; y2 += y; nx += x; ny += y
            if last_cmd in ("C", "S") and last_ctrl:
                x1, y1 = 2 * x - last_ctrl[0], 2 * y - last_ctrl[1]
            else:
                x1, y1 = x, y
            out.append(f"C{fmt(x1)},{fmt(y1)} {fmt(x2)},{fmt(y2)} {fmt(nx)},{fmt(ny)}")
            last_ctrl = (x2, y2)
            x, y = nx, ny
        elif C == "Q":
            x1, y1, nx, ny = nums(4)
            if rel:
                x1 += x; y1 += y; nx += x; ny += y
            out.append(f"Q{fmt(x1)},{fmt(y1)} {fmt(nx)},{fmt(ny)}")
            last_ctrl = (x1, y1)
            x, y = nx, ny
        elif C == "T":
            nx, ny = nums(2)
            if rel:
                nx += x; ny += y
            if last_cmd in ("Q", "T") and last_ctrl:
                x1, y1 = 2 * x - last_ctrl[0], 2 * y - last_ctrl[1]
            else:
                x1, y1 = x, y
            out.append(f"Q{fmt(x1)},{fmt(y1)} {fmt(nx)},{fmt(ny)}")
            last_ctrl = (x1, y1)
            x, y = nx, ny
        elif C == "A":
            rx, ry, rot = nums(3)
            large = int(float(toks[i])); sweep = int(float(toks[i + 1])); i += 2
            nx, ny = nums(2)
            if rel:
                nx += x; ny += y
            out.append(f"A{fmt(rx)},{fmt(ry)} {fmt(rot)} {large} {sweep} {fmt(nx)},{fmt(ny)}")
            x, y = nx, ny
            last_ctrl = None
        else:
            raise ValueError("unsupported command " + cmd)
        last_cmd = C
    return " ".join(out)


def circle(cx, cy, r):
    return (f"M{fmt(cx - r)},{fmt(cy)} A{fmt(r)},{fmt(r)} 0 1 0 {fmt(cx + r)},{fmt(cy)} "
            f"A{fmt(r)},{fmt(r)} 0 1 0 {fmt(cx - r)},{fmt(cy)} Z")


def ellipse(cx, cy, rx, ry):
    return (f"M{fmt(cx - rx)},{fmt(cy)} A{fmt(rx)},{fmt(ry)} 0 1 0 {fmt(cx + rx)},{fmt(cy)} "
            f"A{fmt(rx)},{fmt(ry)} 0 1 0 {fmt(cx - rx)},{fmt(cy)} Z")


def rect(x, y, w, h, rx=0.0, ry=None):
    ry = rx if ry is None else ry
    if rx <= 0:
        return f"M{fmt(x)},{fmt(y)} L{fmt(x + w)},{fmt(y)} L{fmt(x + w)},{fmt(y + h)} L{fmt(x)},{fmt(y + h)} Z"
    return (f"M{fmt(x + rx)},{fmt(y)} L{fmt(x + w - rx)},{fmt(y)} A{fmt(rx)},{fmt(ry)} 0 0 1 {fmt(x + w)},{fmt(y + ry)} "
            f"L{fmt(x + w)},{fmt(y + h - ry)} A{fmt(rx)},{fmt(ry)} 0 0 1 {fmt(x + w - rx)},{fmt(y + h)} "
            f"L{fmt(x + rx)},{fmt(y + h)} A{fmt(rx)},{fmt(ry)} 0 0 1 {fmt(x)},{fmt(y + h - ry)} "
            f"L{fmt(x)},{fmt(y + ry)} A{fmt(rx)},{fmt(ry)} 0 0 1 {fmt(x + rx)},{fmt(y)} Z")


def attr(tag, name, default=None):
    m = re.search(rf'\b{name}="([^"]*)"', tag)
    return m.group(1) if m else default


def main():
    html = HTML.read_text(encoding="utf-8")
    symbols = re.findall(r'<symbol id="(i-[\w-]+)" viewBox="0 0 (\d+) (\d+)">(.*?)</symbol>', html, re.S)
    lines = [
        '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
        "    <!-- Generated by tools/make_icons_xaml.py from the SVG symbols in design/drivesync.html. Do not edit by hand. -->",
        "    <!-- Each icon is drawn with a 1.5-unit stroke on a 20-unit grid (caption-button icons: 1 unit on a 10-unit grid). -->",
    ]
    for sid, w, h, body in symbols:
        figures = []
        for tag in re.findall(r"<(?:path|circle|rect|ellipse)\b[^>]*>", body):
            kind = re.match(r"<(\w+)", tag).group(1)
            if kind == "path":
                figures.append(path_to_abs(attr(tag, "d")))
            elif kind == "circle":
                figures.append(circle(float(attr(tag, "cx")), float(attr(tag, "cy")), float(attr(tag, "r"))))
            elif kind == "ellipse":
                figures.append(ellipse(float(attr(tag, "cx")), float(attr(tag, "cy")), float(attr(tag, "rx")), float(attr(tag, "ry"))))
            elif kind == "rect":
                rx = float(attr(tag, "rx", "0"))
                figures.append(rect(float(attr(tag, "x", "0")), float(attr(tag, "y", "0")),
                                    float(attr(tag, "width")), float(attr(tag, "height")), rx))
        data = " ".join(figures)
        lines.append(f'    <Geometry x:Key="{sid}">{data}</Geometry>')
    lines.append("</ResourceDictionary>")
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Wrote {len(symbols)} icons to {OUT}")


if __name__ == "__main__":
    main()
