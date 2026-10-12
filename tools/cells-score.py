# One-off dev helper: the map object at 0x0087F8xx points at cell arrays whose
# elements are 36 bytes each (vtable 0x007ED5A0 repeats every 36 bytes).
# Try every byte offset inside that 36-byte cell as "the shroud field" and score
# it against the player's minimap screenshot.
#
#   python cells-score.py snapfile mm.ppm [--pad 0]
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
    pad = 0
    if "--pad" in sys.argv:
        pad = int(sys.argv[sys.argv.index("--pad") + 1])

    hi = 0
    for a, d in records(snap):
        hi = max(hi, a + len(d))
    hi += 0x10000
    buf = bytearray(hi)
    for a, d in records(snap):
        buf[a:a + len(d)] = d

    iw, ih, px = load_ppm(ppm)
    x0, y0, x1, y1 = pad, pad, iw - pad, ih - pad
    black = [[0] * iw for _ in range(ih)]
    for y in range(ih):
        base = y * iw * 3
        for x in range(iw):
            k = base + x * 3
            black[y][x] = 1 if (px[k] == 0 and px[k + 1] == 0 and px[k + 2] == 0) else 0

    arrays = [(0x21810024, 40000, 200, 200),
              (0x1EB24E24, 10000, 100, 100),
              (0x2A1D41C4, 5000, 100, 50)]
    # also try the alternate shape for the 5000 one
    arrays.append((0x2A1D41C4, 5000, 50, 100))

    results = []
    for base, count, W, H in arrays:
        if base + count * CELL > hi:
            continue
        for off in range(CELL):
            vals = [buf[base + i * CELL + off] for i in range(count)]
            c = Counter(vals)
            nd = len(c)
            for name, vv in (("id", vals), ("T", None)):
                if name == "T":
                    if W != H:
                        continue
                    vv = [vals[c2 * W + r] for r in range(H) for c2 in range(W)]
                on = []; offv = []
                for r in range(H):
                    for c2 in range(W):
                        sy = y0 + (y1 - y0) * (2 * r + 1) // (2 * H)
                        sx = x0 + (x1 - x0) * (2 * c2 + 1) // (2 * W)
                        if black[sy][sx]:
                            offv.append(vv[r * W + c2])
                        else:
                            on.append(vv[r * W + c2])
                if not on or not offv:
                    continue
                m1 = sum(on) / float(len(on)); m0 = sum(offv) / float(len(offv))
                acc = 0.0
                h1 = Counter(on); h0 = Counter(offv)
                c1 = 0; c0 = 0
                for t in range(256):
                    c1 += h1.get(t, 0); c0 += h0.get(t, 0)
                    a1 = (c1 + (len(offv) - c0)) / float(len(vv))
                    if a1 > acc: acc = a1
                    a2 = ((len(on) - c1) + c0) / float(len(vv))
                    if a2 > acc: acc = a2
                results.append((acc, base, count, W, H, off, nd, m1, m0, name))
    results.sort(key=lambda x: -x[0])
    print("acc     base        n      W   H   offset distinct  meanExplored meanUnexplored  xform")
    seen = set()
    shown = 0
    for acc, base, count, W, H, off, nd, m1, m0, name in results:
        key = (base, off, name)
        if key in seen:
            continue
        seen.add(key)
        print("  %.4f  0x%08X  %-6d %3d %3d  +0x%02X  %-3d     %6.1f      %6.1f        %s"
              % (acc, base, count, W, H, off, nd, m1, m0, name))
        shown += 1
        if shown >= 45:
            break


if __name__ == "__main__":
    main()
