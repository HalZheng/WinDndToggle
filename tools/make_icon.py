"""生成 app.ico（32x32 PNG-compressed ICO），不依赖 PIL。"""
import math
import struct
import zlib
from pathlib import Path

SIZE = 32
SS = 3  # 超采样倍数


def in_circle(x, y, cx, cy, r):
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def coverage(x, y, cx, cy, r):
    hits = 0
    for i in range(SS):
        for j in range(SS):
            sx = x + (i + 0.5) / SS
            sy = y + (j + 0.5) / SS
            if in_circle(sx, sy, cx, cy, r):
                hits += 1
    return hits / (SS * SS)


def build_pixels():
    rows = []
    for y in range(SIZE):
        row = bytearray()
        for x in range(SIZE):
            a_outer = coverage(x, y, 15.5, 15.5, 11.5)
            a_cut = coverage(x, y, 24.5, 11.5, 11.5)
            a = max(0.0, a_outer - a_cut)

            r, g, b = 139, 92, 246  # 紫色月亮

            d = coverage(x, y, 25.5, 25.5, 5.5)
            if d > 0:
                r = int(r * (1 - d) + 239 * d)
                g = int(g * (1 - d) + 68 * d)
                b = int(b * (1 - d) + 68 * d)
                a = a + d * (1 - a)

            row += bytes((r, g, b, int(round(a * 255))))
        rows.append(bytes(row))
    return rows


def png_bytes(rows):
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, data):
        return (
            struct.pack(">I", len(data))
            + tag
            + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
        )

    ihdr = struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def main():
    png = png_bytes(build_pixels())
    ico = (
        struct.pack("<HHH", 0, 1, 1)
        + struct.pack("<BBBBHHII", SIZE, SIZE, 0, 0, 1, 32, len(png), 22)
        + png
    )
    out = Path(__file__).resolve().parent.parent / "src" / "WinDndToggle" / "app.ico"
    out.write_bytes(ico)
    print(f"{out} = {len(ico)} bytes")


if __name__ == "__main__":
    main()
