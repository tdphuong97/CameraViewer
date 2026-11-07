"""Draws the Camera Viewer icon and writes src/Cv.App/Assets/cv.ico.

    python -I build/make-icon.py [--preview DIR]

Pure standard library (no Pillow): each size is rendered from signed distance functions with analytic
antialiasing, so small sizes stay sharp. Small frames are 32-bit BMP (read by every Windows component), 256 px is PNG.
Lens ring detail from 40 px up. --preview DIR also writes PNG previews (256 px, and the small sizes enlarged on light and dark backgrounds).
"""
import math
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

TILE_TOP = (0x1E, 0x22, 0x2B)
TILE_BOTTOM = (0x0F, 0x11, 0x15)
BORDER = (0x2A, 0x2F, 0x3A)
ACCENT = (0x3B, 0x82, 0xF6)
ACCENT_LIGHT = (0x60, 0xA5, 0xFA)


def clamp(x, lo=0.0, hi=1.0):
    return lo if x < lo else hi if x > hi else x


def sd_round_box(px, py, x0, y0, x1, y1, r):
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    hx, hy = (x1 - x0) / 2 - r, (y1 - y0) / 2 - r
    qx, qy = abs(px - cx) - hx, abs(py - cy) - hy
    return math.hypot(max(qx, 0), max(qy, 0)) + min(max(qx, qy), 0) - r


def sd_polygon(px, py, pts):
    """Exact signed distance to a simple polygon (negative inside)."""
    d = (px - pts[0][0]) ** 2 + (py - pts[0][1]) ** 2
    s = 1.0
    n = len(pts)
    j = n - 1
    for i in range(n):
        ax, ay = pts[i]
        bx, by = pts[j]
        ex, ey = bx - ax, by - ay
        wx, wy = px - ax, py - ay
        t = clamp((wx * ex + wy * ey) / (ex * ex + ey * ey))
        dx, dy = wx - ex * t, wy - ey * t
        d = min(d, dx * dx + dy * dy)
        c1, c2, c3 = py >= ay, py < by, ex * wy > ey * wx
        if (c1 and c2 and c3) or (not c1 and not c2 and not c3):
            s = -s
        j = i
    return s * math.sqrt(d)


def sd_circle(px, py, cx, cy, r):
    return math.hypot(px - cx, py - cy) - r


def cover(d):
    """Pixel coverage of a shape from its signed distance in pixels (box filter approximation)."""
    return clamp(0.5 - d)


def over(dst, src, a):
    """Composites colour src with alpha a over premultiplied-free RGBA dst (floats 0..1)."""
    if a <= 0:
        return dst
    r, g, b, da = dst
    out_a = a + da * (1 - a)
    if out_a <= 0:
        return (0.0, 0.0, 0.0, 0.0)
    k = da * (1 - a)
    return ((src[0] * a + r * k) / out_a, (src[1] * a + g * k) / out_a, (src[2] * a + b * k) / out_a, out_a)


def lerp(c0, c1, t):
    return tuple(c0[i] + (c1[i] - c0[i]) * t for i in range(3))


def layout(s):
    """Geometry in pixels for size s. Small sizes snap the glyph to whole pixels."""
    snap = s <= 48
    rnd = (lambda v: float(round(v))) if snap else (lambda v: v)
    tile_r = s * 0.22
    border_w = max(1.0, s / 64)
    # Material "videocam" proportions: body 14 x 12, lens 4 wide, lens 3 tall at the body and 11 tall at the tip.
    gw = s * (0.70 if s <= 24 else 0.64)
    gh = gw * 12 / 18
    gx0 = rnd((s - gw) / 2)
    gx1 = rnd((s + gw) / 2)
    gy0 = rnd((s - gh) / 2)
    gy1 = rnd((s + gh) / 2)
    gw, gh = gx1 - gx0, gy1 - gy0
    bx1 = rnd(gx0 + gw * 14 / 18)
    body = (gx0, gy0, bx1, gy1, max(1.0, gh * 0.14) if s > 16 else 1.0)
    cy = (gy0 + gy1) / 2
    gap = 0.0 if s <= 24 else gw * 0.035
    # Small sizes: wider lens neck and full-height tip so the shape survives at 16 px.
    neck = gh * (4.5 if s <= 24 else 3) / 24
    tip = gh * (12 if s <= 24 else 11) / 24
    lens = [(bx1 + gap, cy - neck), (gx1, cy - tip), (gx1, cy + tip), (bx1 + gap, cy + neck)]
    lens_r = 0.0 if s <= 24 else gh * 0.06
    detail = s >= 40
    return tile_r, border_w, body, lens, lens_r, detail


def render(s):
    tile_r, border_w, body, lens, lens_r, detail = layout(s)
    bx0, by0, bx1, by1, br = body
    lcx, lcy = bx0 + (bx1 - bx0) * 0.42, (by0 + by1) / 2
    ring_r = (by1 - by0) * 0.27
    rgba = bytearray(s * s * 4)
    for y in range(s):
        py = y + 0.5
        for x in range(s):
            px = x + 0.5
            px_ = (0.0, 0.0, 0.0, 0.0)
            d_tile = sd_round_box(px, py, 0, 0, s, s, tile_r)
            a_tile = cover(d_tile)
            if a_tile > 0:
                fill = lerp(TILE_TOP, TILE_BOTTOM, clamp(py / s))
                px_ = over(px_, tuple(c / 255 for c in fill), a_tile)
                # Thin border on the inside of the tile edge.
                a_b = clamp(cover(d_tile) - cover(d_tile + border_w))
                px_ = over(px_, tuple(c / 255 for c in BORDER), a_b)
                # Glyph: body and lens, a subtle top-to-bottom light on big sizes.
                d_body = sd_round_box(px, py, bx0, by0, bx1, by1, br)
                d_lens = sd_polygon(px, py, lens) - lens_r
                a_g = cover(min(d_body, d_lens))
                if a_g > 0:
                    g = clamp((py - by0) / (by1 - by0)) if s >= 64 else 1.0
                    col = lerp(ACCENT_LIGHT, ACCENT, 0.35 + 0.65 * g) if s >= 64 else ACCENT
                    px_ = over(px_, tuple(c / 255 for c in col), a_g * a_tile)
                if detail:
                    # Lens ring cut out of the body, with a blue pupil.
                    ring_w = max(1.0, ring_r * 0.42)
                    d_ring_outer = sd_circle(px, py, lcx, lcy, ring_r)
                    a_hole = cover(d_ring_outer) * cover(d_body)
                    hole = lerp(TILE_TOP, TILE_BOTTOM, clamp(py / s))
                    px_ = over(px_, tuple(c / 255 for c in hole), a_hole)
                    a_pupil = cover(sd_circle(px, py, lcx, lcy, ring_r - ring_w))
                    px_ = over(px_, tuple(c / 255 for c in ACCENT_LIGHT), a_pupil)
            r, g, b, a = px_
            i = (y * s + x) * 4
            rgba[i:i + 4] = bytes((round(r * 255), round(g * 255), round(b * 255), round(a * 255)))
    return bytes(rgba)


def png(w, h, rgba):
    raw = b"".join(b"\x00" + rgba[y * w * 4:(y + 1) * w * 4] for y in range(h))

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def bmp_frame(w, h, rgba):
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray()
        for x in range(w):
            r, g, b, a = rgba[(y * w + x) * 4:(y * w + x) * 4 + 4]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    mask_stride = ((w + 31) // 32) * 4
    return header + b"".join(rows) + bytes(mask_stride * h)


def write_ico(path, frames):
    ico = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    blobs = b""
    for size, blob in frames:
        dim = 0 if size >= 256 else size
        ico += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset + len(blobs))
        blobs += blob
    with open(path, "wb") as f:
        f.write(ico + blobs)


def preview(folder, images):
    """Small sizes enlarged (nearest neighbour) on a light and a dark strip, plus the 256 px frame."""
    os.makedirs(folder, exist_ok=True)
    with open(os.path.join(folder, "icon-256.png"), "wb") as f:
        f.write(png(256, 256, images[256]))
    scale = {16: 8, 20: 6, 24: 5, 32: 4, 40: 3, 48: 2, 64: 2}
    pad = 16
    width = pad + sum(s * k + pad for s, k in scale.items()) + sum(s + pad for s in scale)
    height = pad + 128 + pad
    for name, bg in (("light", (243, 243, 243)), ("dark", (32, 32, 32))):
        canvas = bytearray(bytes(bg + (255,)) * (width * height))
        x = pad

        def blit(img, s, k, x0, y0):
            for y in range(s * k):
                for xx in range(s * k):
                    i = ((y // k) * s + xx // k) * 4
                    r, g, b, a = img[i:i + 4]
                    j = ((y0 + y) * width + x0 + xx) * 4
                    t = a / 255
                    for c in range(3):
                        canvas[j + c] = round(canvas[j + c] * (1 - t) + (r, g, b)[c] * t)

        for s, k in scale.items():
            blit(images[s], s, k, x, pad)
            x += s * k + pad
        for s in scale:
            blit(images[s], s, 1, x, pad)
            x += s + pad
        with open(os.path.join(folder, f"icon-small-{name}.png"), "wb") as f:
            f.write(png(width, height, bytes(canvas)))
    print("previews in", folder)


def main():
    images = {s: render(s) for s in SIZES}
    frames = [(s, png(s, s, images[s]) if s >= 256 else bmp_frame(s, s, images[s])) for s in SIZES]
    path = os.path.join(ROOT, "src", "Cv.App", "Assets", "cv.ico")
    write_ico(path, frames)
    print("wrote", path, "sizes", SIZES)
    if "--preview" in sys.argv:
        preview(sys.argv[sys.argv.index("--preview") + 1], images)


if __name__ == "__main__":
    main()
