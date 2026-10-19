"""
Generates NoIntrovert's app icon: an original pixel-art retro joystick
(orange ball top, dark base, two fire buttons) on a dark rounded tile.

The icon is drawn on two pixel grids: a 32x32 master, scaled up by whole
numbers for the large sizes, and a hand-tuned 16x16 for the small ones
(16 and 48 px). Writes, with no image libraries:

  NoIntrovert/Assets/nointrovert.ico     Windows (16-256 px, PNG entries)
  NoIntrovert/Assets/nointrovert-64.png  window header logo
  packaging/macos/NoIntrovert.icns       macOS (16-1024 px)
  packaging/icon/png/nointrovert-<n>.png every size, for anything else

Run from anywhere:  python packaging/icon/make_icon.py
"""
import math
import os
import struct
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))

PALETTE = {
    ".": None,           # transparent (outside the tile's rounded corners)
    "E": "#2f3440",      # tile edge
    "B": "#1b1e26",      # tile background
    "K": "#13151a",      # base outline
    "T": "#454b58",      # base top face
    "F": "#2b2f39",      # base front face
    "S": "#c3c8d1",      # shaft
    "s": "#8a8f9a",      # shaft shadow side
    "c": "#5a606c",      # collar where the shaft meets the base
    "O": "#e0703a",      # ball
    "R": "#a8461f",      # ball rim
    "L": "#f39a5b",      # ball highlight
    "W": "#ffffff",      # glint
    "Y": "#ffd166",      # fire button
    "y": "#c9952e",      # fire button shadow
}

# Hand-tuned 16x16, so the smallest sizes stay crisp instead of being a
# blurred 32x32.
GRID_16 = [
    "..EEEEEEEEEEEE..",
    ".EBBBBRRRRBBBBE.",
    "EBBBBRWLOORBBBBE",
    "EBBBBRLOOORBBBBE",
    "EBBBBROOOORBBBBE",
    "EBBBBBRRRRBBBBBE",
    "EBBBBBBSsBBBBBBE",
    "EBBBBBBSsBBBBBBE",
    "EBBBBBBSsBBBBBBE",
    "EBBBBBccccBBBBBE",
    "EBBKKKKKKKKKKBBE",
    "EBKTYYTTTTYYTKBE",
    "EBKFFFFFFFFFFKBE",
    "EBKFFFFFFFFFFKBE",
    ".EBKKKKKKKKKKBE.",
    "..EEEEEEEEEEEE..",
]


def grid_32():
    """The 32x32 master: tile, base with two buttons, shaft, collar, ball."""
    n = 32
    g = [["." for _ in range(n)] for _ in range(n)]

    def put(x, y, c):
        x, y = int(round(x)), int(round(y))
        if 0 <= x < n and 0 <= y < n:
            g[y][x] = c

    # Rounded tile.
    r = 6
    for y in range(n):
        for x in range(n):
            cx = min(max(x, r), n - 1 - r)
            cy = min(max(y, r), n - 1 - r)
            d = math.hypot(x - cx, y - cy)
            if d <= r + 0.3:
                g[y][x] = "E" if d > r - 0.7 else "B"

    # Base: a box with a lighter top face and a darker front.
    for y in range(20, 28):
        for x in range(5, 27):
            if y in (20, 27) and x in (5, 26):
                continue
            border = y in (20, 27) or x in (5, 26)
            g[y][x] = "K" if border else ("T" if y <= 22 else "F")
    for x in range(6, 26):
        g[23][x] = "K"

    # Shaft (with a shadow side) and its collar.
    for y in range(10, 21):
        put(15, y, "S")
        put(16, y, "s")
    for x in range(13, 19):
        put(x, 20, "c")
    for x in range(14, 18):
        put(x, 19, "c")

    # Fire buttons on the top face.
    for cx in (8, 21):
        for x in range(cx - 1, cx + 3):
            put(x, 21, "Y")
            put(x, 22, "y")
        put(cx - 1, 21, "y")
        put(cx + 2, 21, "y")

    # Ball top: rim, highlight crescent and a glint.
    bx, by, br = 15.5, 8.0, 4.6
    for y in range(n):
        for x in range(n):
            d = math.hypot(x - bx, y - by)
            if d > br:
                continue
            c = "R" if d > br - 1 else "O"
            a = math.atan2(y - by, x - bx)
            if d < br - 1 and d > br * 0.35 and -2.7 < a < -1.4:
                c = "L"
            g[y][x] = c
    put(bx - 1.6, by - 1.9, "W")
    put(bx - 2.5, by - 1.1, "W")
    return ["".join(row) for row in g]


def rgba(grid, scale):
    """Expands a grid to RGBA rows, each pixel becoming a scale x scale block."""
    rows = []
    for line in grid:
        row = bytearray()
        for ch in line:
            colour = PALETTE[ch]
            if colour is None:
                px = b"\x00\x00\x00\x00"
            else:
                px = bytes(int(colour[i:i + 2], 16) for i in (1, 3, 5)) + b"\xff"
            row += px * scale
        rows.extend([bytes(row)] * scale)
    return rows


def png(rows):
    width = len(rows[0]) // 4
    height = len(rows)
    raw = b"".join(b"\x00" + r for r in rows)  # filter type 0 per row

    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def icon_png(size):
    """PNG bytes for one size: whole-number scaling of the 16 or 32 grid."""
    if size % 32 == 0:
        return png(rgba(grid_32(), size // 32))
    if size % 16 == 0:
        return png(rgba(GRID_16, size // 16))
    raise ValueError(f"{size} px is not a multiple of 16")


def write_ico(path, sizes):
    images = [icon_png(s) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    for size, data in zip(sizes, images):
        dim = 0 if size >= 256 else size  # 0 means 256 in the ICO directory
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(path, "wb") as f:
        f.write(header + entries + b"".join(images))


def write_icns(path):
    # PNG-based ICNS element types and the pixel size each holds.
    types = [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256),
             ("ic09", 512), ("ic10", 1024), ("ic11", 32), ("ic12", 64), ("ic13", 256), ("ic14", 512)]
    body = b""
    for kind, size in types:
        data = icon_png(size)
        body += kind.encode("ascii") + struct.pack(">I", len(data) + 8) + data
    with open(path, "wb") as f:
        f.write(b"icns" + struct.pack(">I", len(body) + 8) + body)


def main():
    for row in GRID_16 + grid_32():
        bad = set(row) - PALETTE.keys()
        if bad:
            raise SystemExit(f"unknown colour codes {bad}")
    if any(len(r) != 16 for r in GRID_16) or len(GRID_16) != 16:
        raise SystemExit("GRID_16 must be 16 rows of 16")

    png_dir = os.path.join(HERE, "png")
    os.makedirs(png_dir, exist_ok=True)
    for size in (16, 32, 48, 64, 128, 256, 512, 1024):
        with open(os.path.join(png_dir, f"nointrovert-{size}.png"), "wb") as f:
            f.write(icon_png(size))

    assets = os.path.join(ROOT, "NoIntrovert", "Assets")
    write_ico(os.path.join(assets, "nointrovert.ico"), [16, 32, 48, 64, 128, 256])
    with open(os.path.join(assets, "nointrovert-64.png"), "wb") as f:
        f.write(icon_png(64))
    write_icns(os.path.join(ROOT, "packaging", "macos", "NoIntrovert.icns"))
    print("wrote nointrovert.ico, nointrovert-64.png, NoIntrovert.icns and packaging/icon/png/*")


if __name__ == "__main__":
    main()
