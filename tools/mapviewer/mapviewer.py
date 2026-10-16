"""Vẽ lưới collision của các map GoPet ra ảnh PNG, đọc thẳng từ sharedassets0.assets (không chạy game).

Định dạng file maps/<id>.dat (giải mã từ qx.gxg, RVA 0x11A2940; số nguyên big-endian):
    s8  a, s8 b                 số asset = a + b
    (s16 id, s8 type) * (a+b)   MapAsset
    s8  W, s8 H, s8 L           kích thước lưới (ô) và số lớp tile
    u8[W*H] * L                 các lớp tile
    u8[W*H]                     lưới collision (class qv)
    ...                         object, trigger/portal, entrance (chưa giải mã)

Mỗi ô 24x24 px (qv.gvw, RVA 0x11A15E0). Byte collision chia ô thành 4 góc 12x12:
    bit 8 = trên-trái, 4 = trên-phải, 2 = dưới-trái, 1 = dưới-phải; bit bật = bị chặn.
    0 = đi được hết, 15 = chặn hết. Ngoài biên map coi như 15.

Cách dùng:
    python mapviewer.py <sharedassets0.assets> <thư_mục_ra> [--scale N] [--map 12]
"""
import argparse
import os
import sys
import zlib

from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from unityfile import SerializedFile  # noqa: E402

CELL = 24
QUADS = [(8, 0, 0), (4, 1, 0), (2, 0, 1), (1, 1, 1)]  # (bit, qx, qy)


def s8(v):
    return v - 256 if v > 127 else v


def parse_map(data):
    a, b = s8(data[0]), s8(data[1])
    p = 2 + 3 * (a + b)
    w, h, layers = s8(data[p]), s8(data[p + 1]), s8(data[p + 2])
    p += 3
    tiles = [data[p + i * w * h: p + (i + 1) * w * h] for i in range(layers)]
    p += layers * w * h
    collision = data[p: p + w * h]
    if len(collision) != w * h:
        raise ValueError('file map bị cắt ngắn')
    return dict(w=w, h=h, tiles=tiles, collision=collision)


def tile_color(t):
    if t == 0:
        return (24, 26, 30)
    c = zlib.crc32(bytes([t])) & 0xFFFFFF
    r, g, b = (c >> 16) & 255, (c >> 8) & 255, c & 255
    return (60 + r // 4, 70 + g // 4, 60 + b // 4)


def render(m, scale):
    w, h = m['w'], m['h']
    cs = CELL * scale // 2 * 2
    half = cs // 2
    img = Image.new('RGB', (w * cs, h * cs), (24, 26, 30))
    d = ImageDraw.Draw(img, 'RGBA')
    base = m['tiles'][0] if m['tiles'] else bytes(w * h)
    for y in range(h):
        for x in range(w):
            x0, y0 = x * cs, y * cs
            d.rectangle([x0, y0, x0 + cs - 1, y0 + cs - 1], fill=tile_color(base[y * w + x]))
            v = m['collision'][y * w + x]
            for bit, qx, qy in QUADS:
                if v & bit:
                    qx0, qy0 = x0 + qx * half, y0 + qy * half
                    d.rectangle([qx0, qy0, qx0 + half - 1, qy0 + half - 1], fill=(230, 50, 50, 190))
    grid = (255, 255, 255, 28)
    for x in range(w + 1):
        d.line([x * cs, 0, x * cs, h * cs], fill=grid)
    for y in range(h + 1):
        d.line([0, y * cs, w * cs, y * cs], fill=grid)
    return img


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('assets')
    ap.add_argument('out')
    ap.add_argument('--scale', type=int, default=1, help='hệ số phóng (1 = 24px/ô như trong game)')
    ap.add_argument('--map', help='chỉ vẽ một map, vd. 12')
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding='utf-8')

    os.makedirs(args.out, exist_ok=True)
    sf = SerializedFile(args.assets)
    maps = {name[:-4]: data for _, name, data in sf.text_assets()
            if name.endswith('.dat') and name[:-4].isdigit()}
    if args.map:
        maps = {args.map: maps[args.map]}
    for mid in sorted(maps, key=int):
        m = parse_map(maps[mid])
        path = os.path.join(args.out, f'map_{mid}.png')
        render(m, args.scale).save(path)
        blocked = sum(bin(v & 15).count('1') for v in m['collision'])
        print(f'map {mid:>4}: {m["w"]}x{m["h"]} ô, {blocked / (4 * m["w"] * m["h"]):.0%} bị chặn -> {path}')


if __name__ == '__main__':
    main()
