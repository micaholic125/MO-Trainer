# One-off dev helper: the previous 40 000-cell hunt only tried page-aligned
# starts, so it could only see a real map array if the array happened to begin
# on a page boundary.  Any page boundary *inside* the array yields a grid that
# is the true map shifted by K cells -- which never matches the screenshot.
#
# Fix: for each hit, walk the start back by whole rows (200 cells) at a time,
# which is the only shift that keeps a valid 200-wide layout, and re-score.
#
#   python align-map.py snapfile mm.ppm hits.txt [--w 200] [--rows 24]
#
# Read-only.
import sys
from collections import Counter

MAGIC = b"MOSNAP01"
PRE = 320          # template samples used for the first pass


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
    snap = sys.argv[1]; ppm = sys.argv[2]; hits = sys.argv[3]
    W = 200; H = 200
    if "--w" in sys.argv:
        W = int(sys.argv[sys.argv.index("--w") + 1])
    if "--h" in sys.argv:
        H = int(sys.argv[sys.argv.index("--h") + 1])
    maxrows = 24
    if "--rows" in sys.argv:
        maxrows = int(sys.argv[sys.argv.index("--rows") + 1])
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
    print("loaded %d MB" % (hi // 1048576))

    iw, ih, px = load_ppm(ppm)
    x0, y0, x1, y1 = pad, pad, iw - pad, ih - pad
    # template sampled sparsely: (row, col) pairs spread over the grid
    pts = []
    n = int(PRE ** 0.5)
    for i in range(n):
        for j in range(n):
            r = (2 * i + 1) * H // (2 * n)
            c = (2 * j + 1) * W // (2 * n)
            sy = y0 + (y1 - y0) * (2 * r + 1) // (2 * H)
            sx = x0 + (x1 - x0) * (2 * c + 1) // (2 * W)
            k = (sy * iw + sx) * 3
            exp = 0 if (px[k] == 0 and px[k + 1] == 0 and px[k + 2] == 0) else 1
            pts.append((r, c, exp))
    print("template sample points: %d, explored share %.3f"
          % (len(pts), sum(p[2] for p in pts) / float(len(pts))))

    cands = []
    for line in open(hits, encoding="utf-8"):
        p = line.split()
        if len(p) >= 4:
            try:
                cands.append((float(p[0]), int(p[2], 16), int(p[3])))
            except ValueError:
                pass
    print("hits: %d" % len(cands))

    results = []
    for share, base, stride in cands:
        for m in range(maxrows):
            A = base - W * m * stride
            if A < 0:
                break
            good = 0
            tot = 0
            for (r, c, exp) in pts:
                rr = r - m
                if rr < 0:
                    continue
                a = A + (rr * W + c) * stride
                if a >= hi:
                    continue
                tot += 1
                v = buf[a]
                if (exp == 1 and v != 0) or (exp == 0 and v == 0):
                    good += 1
            if tot < len(pts) // 2:
                continue
            results.append((good / float(tot), base, stride, m, A, tot))
    results.sort(key=lambda x: -x[0])
    print("acc     base        stride  rowshift  true-start   n")
    seen = set()
    shown = 0
    for acc, base, stride, m, A, tot in results:
        if (A, stride) in seen:
            continue
        seen.add((A, stride))
        print("  %.4f  0x%08X  %-4d  %-8d  0x%08X  %d" % (acc, base, stride, m, A, tot))
        shown += 1
        if shown >= 40:
            break


if __name__ == "__main__":
    main()
