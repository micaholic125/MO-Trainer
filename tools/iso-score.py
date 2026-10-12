# One-off dev helper: RA2/YR maps are isometric -- the cell grid (X, Y) is drawn
# at screen (X-Y, X+Y), so a square 200x200 map appears as a diamond, not a
# rectangle.  The earlier scorers all assumed a row-major rectangle, which can
# never match.  This one samples the cell array through the iso transform, in
# all 8 axis/flip/mirror variants.
#
#   python iso-score.py snapfile mm.ppm [--w 200] [--h 200]
#
# Read-only.
import sys
from collections import Counter

MAGIC = b"MOSNAP01"
CELL = 36


def records(path):
    raw = open(path, "rb").read()
    if raw[:8] != MAGIC:
        raise SystemExit("bad magic")
    pos = 8
    pos += 4
    sl = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4 + sl
    while pos < len(raw):
        k = raw[pos]; pos += 1
        if k == 0:
            break
        if k == 1:
            pos += 24
            continue
        a = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
        ln = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
        yield a, raw[pos:pos + ln]
        pos += ln


def load_ppm(path):
    raw = open(path, "rb").read()
    pos = 0
    f = []
    while len(f) < 4:
        while raw[pos:pos + 1].isspace():
            pos += 1
        s = pos
        while not raw[pos:pos + 1].isspace():
            pos += 1
        f.append(raw[s:pos])
    pos += 1
    w = int(f[1]); h = int(f[2])
    return w, h, raw[pos:pos + w * h * 3]


def main():
    snap = sys.argv[1]; ppm = sys.argv[2]
    W = 200; H = 200
    if "--w" in sys.argv:
        W = int(sys.argv[sys.argv.index("--w") + 1])
    if "--h" in sys.argv:
        H = int(sys.argv[sys.argv.index("--h") + 1])
    cell = CELL
    if "--cell" in sys.argv:
        cell = int(sys.argv[sys.argv.index("--cell") + 1])

    hi = 0
    for a, d in records(snap):
        hi = max(hi, a + len(d))
    hi += 0x10000
    buf = bytearray(hi)
    for a, d in records(snap):
        buf[a:a + len(d)] = d

    iw, ih, px = load_ppm(ppm)

    arrays = [(0x21810024, 40000), (0x1EB24E24, 10000), (0x2A1D41C4, 5000)]
    results = []
    for base, count in arrays:
        if base + count * cell > hi:
            continue
        # 这个数组自己的网格边长（数组可能是全分辨率，也可能是低分辨率的副本）
        S = int(round(count ** 0.5))
        if S * S != count:
            S = 0
        for off in range(cell):
            # 取整格数组的该偏移
            vals = [buf[base + i * cell + off] for i in range(count)]
            for variant in range(8):
                swap = variant & 1
                fx = (variant >> 1) & 1
                fy = (variant >> 2) & 1
                on = []; offv = []
                for r in range(0, ih):
                    v = (2 * r + 1) / (2.0 * ih)
                    for c in range(0, iw):
                        k = (r * iw + c) * 3
                        unexplored = (px[k] == 0 and px[k + 1] == 0 and px[k + 2] == 0)
                        u = (2 * c + 1) / (2.0 * iw)
                        # iso：格坐标 (X,Y) 在屏幕上是 (X-Y, X+Y)。
                        # 先算归一化到 [0,1] 的格坐标，再按该数组自己的边长取样。
                        sx = (W + H) * u - H
                        sy = (W + H) * v
                        nx = (sx + sy) / (2.0 * W)
                        ny = (sy - sx) / (2.0 * H)
                        if swap:
                            nx, ny = ny, nx
                        if fx:
                            nx = 1.0 - nx
                        if fy:
                            ny = 1.0 - ny
                        if nx < 0 or nx >= 1 or ny < 0 or ny >= 1:
                            continue
                        xi = int(nx * W); yi = int(ny * H)
                        if S:
                            xi = xi * S // W
                            yi = yi * S // H
                            val = vals[yi * S + xi]
                        else:
                            # 5000 格：试 100x50
                            if xi >= 100 or yi >= 50:
                                continue
                            val = vals[yi * 100 + xi]
                        if unexplored:
                            offv.append(val)
                        else:
                            on.append(val)
                if len(on) < 200 or len(offv) < 200:
                    continue
                h1 = Counter(on); h0 = Counter(offv)
                acc = 0.0
                c1 = 0; c0 = 0
                n = len(on) + len(offv)
                for t in range(256):
                    c1 += h1.get(t, 0); c0 += h0.get(t, 0)
                    a1 = (c1 + (len(offv) - c0)) / float(n)
                    if a1 > acc: acc = a1
                    a2 = ((len(on) - c1) + c0) / float(n)
                    if a2 > acc: acc = a2
                results.append((acc, base, count, off, variant,
                                sum(on) / float(len(on)), sum(offv) / float(len(offv))))
    results.sort(key=lambda x: -x[0])
    print("acc     base        n      offset variant  meanExplored meanUnexplored")
    for acc, base, count, off, variant, m1, m0 in results[:30]:
        print("  %.4f  0x%08X  %-6d +0x%02X   %d(swap%d fx%d fy%d)  %7.1f %7.1f"
              % (acc, base, count, off, variant, variant & 1, (variant >> 1) & 1,
                 (variant >> 2) & 1, m1, m0))


if __name__ == "__main__":
    main()
