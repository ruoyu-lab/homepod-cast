"""生成 app.ico（纯标准库，多尺寸 PNG 内嵌 ICO）。用法：python tools/make_icon.py"""
import math, struct, zlib, os

def sdf_round_rect(x, y, cx, cy, hw, hh, r):
    dx = abs(x - cx) - (hw - r)
    dy = abs(y - cy) - (hh - r)
    ox, oy = max(dx, 0.0), max(dy, 0.0)
    return math.hypot(ox, oy) + min(max(dx, dy), 0.0) - r

def mix(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(len(a)))

def over(dst, src, alpha):
    # src/dst 为非预乘 RGBA(0-1)
    a = alpha + dst[3] * (1 - alpha)
    if a <= 0:
        return (0, 0, 0, 0)
    rgb = tuple((src[i] * alpha + dst[i] * dst[3] * (1 - alpha)) / a for i in range(3))
    return rgb + (a,)

def cover(d, aa):
    return min(1.0, max(0.0, 0.5 - d / aa))

def render(size):
    ss = 4
    px = bytearray()
    aa = 1.0 / size
    for j in range(size):
        px.append(0)
        for i in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sj in range(ss):
                for si in range(ss):
                    x = (i + (si + 0.5) / ss) / size
                    y = (j + (sj + 0.5) / ss) / size
                    c = (0, 0, 0, 0)
                    # 背景：圆角方块，上浅下深
                    d = sdf_round_rect(x, y, 0.5, 0.5, 0.47, 0.47, 0.22)
                    a = cover(d, aa)
                    if a > 0:
                        c = over(c, mix((0.20, 0.20, 0.22), (0.09, 0.09, 0.10), y) + (1,), a)
                    # HomePod 机身
                    d = sdf_round_rect(x, y, 0.5, 0.55, 0.22, 0.27, 0.15)
                    a = cover(d, aa)
                    if a > 0:
                        c = over(c, mix((0.97, 0.97, 0.98), (0.80, 0.80, 0.83), (y - 0.28) / 0.54) + (1,), a)
                    # 顶部发光点
                    d = math.hypot(x - 0.5, y - 0.37) - 0.075
                    a = cover(d, aa)
                    if a > 0:
                        t = min(1.0, max(0.0, (x - 0.42) / 0.16))
                        c = over(c, mix((0.55, 0.36, 0.96), (0.20, 0.62, 1.00), t) + (1,), a)
                    for k in range(4):
                        acc[k] += c[k] * (c[3] if k < 3 else 1)
            n = ss * ss
            a = acc[3] / n
            rgb = [acc[k] / n / a if a > 0 else 0 for k in range(3)]
            px += bytes([round(rgb[0] * 255), round(rgb[1] * 255), round(rgb[2] * 255), round(a * 255)])
    return png(size, bytes(px))

def png(size, raw):
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")

sizes = [16, 20, 24, 32, 40, 48, 64, 256]
images = [render(s) for s in sizes]
out = struct.pack("<HHH", 0, 1, len(images))
offset = 6 + 16 * len(images)
for s, img in zip(sizes, images):
    out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(img), offset)
    offset += len(img)
out += b"".join(images)
root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
open(os.path.join(root, "app.ico"), "wb").write(out)
# README 用的大图标
os.makedirs(os.path.join(root, "docs", "images"), exist_ok=True)
open(os.path.join(root, "docs", "images", "logo.png"), "wb").write(images[-1])
print("wrote app.ico", len(out), "bytes and docs/images/logo.png")
