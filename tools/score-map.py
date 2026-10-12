# One-off dev helper: score candidate 40 000-cell arrays against the player's
# minimap screenshot.  A correct shroud array must separate "explored" pixels
# from "unexplored" pixels with almost no overlap.
#
#   python score-map.py snapfile mm.ppm hits.txt [--w 200] [--h 200]
#
# Read-only.
import sys
from collections import Counter

MAGIC = b"MOSNAP01"


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
    fields = []
    while len(fields) < 4:
        while raw[pos:pos + 1].isspace():
            pos += 1
        if raw[pos:pos + 1] == b"#":
            while raw[pos:pos + 1] not in (b"\n", b""):
                pos += 1
            continue
        s = pos
        while not raw[pos:pos + 1].isspace():
            pos += 1
        fields.append(raw[s:pos])
    pos += 1
    w = int(fields[1]); h = int(fields[2])
    return w, h, raw[pos:pos + w * h * 3]


def template(path, tw, th, pad):
    """1 = explored pixel, 0 = pure black (unexplored).  Resampled to tw x th."""
    w, h, px = load_ppm(path)
    x0, y0, x1, y1 = pad, pad, w - pad, h - pad
    grid = []
    for r in range(th):
        row = []
        sy = y0 + (y1 - y0) * (2 * r + 1) // (2 * th)
        for c in range(tw):
            sx = x0 + (x1 - x0) * (2 * c + 1) // (2 * tw)
            i = (sy * w + sx) * 3
            row.append(0 if (px[i] == 0 and px[i + 1] == 0 and px[i + 2] == 0) else 1)
        grid.append(row)
    return grid


def score(vals, tpl):
    """Return (accuracy, mean1, mean0) for the best single threshold."""
    n = len(vals)
    on = [v for v, t in zip(vals, tpl) if t]
    off = [v for v, t in zip(vals, tpl) if not t]
    if not on or not off:
        return 0.0, 0, 0
    m1 = sum(on) / float(len(on))
    m0 = sum(off) / float(len(off))
    lo, hi = (0, 255)
    best = 0.0
    h1 = Counter(on)
    h0 = Counter(off)
    c1 = 0; c0 = 0
    for t in range(256):
        c1 += h1.get(t, 0)
        c0 += h0.get(t, 0)
        acc = (c1 + (len(off) - c0)) / float(n)      # <=t is "explored"
        if acc > best:
            best = acc
        acc2 = ((len(on) - c1) + c0) / float(n)
        if acc2 > best:
            best = acc2
    return best, m1, m0


def main():
    snap = sys.argv[1]; ppm = sys.argv[2]
    W = 200; H = 200
    if "--w" in sys.argv:
        W = int(sys.argv[sys.argv.index("--w") + 1])
    if "--h" in sys.argv:
        H = int(sys.argv[sys.argv.index("--h") + 1])

    hi = 0; sp = []
    for a, d in records(snap):
        sp.append((a, len(d))); hi = max(hi, a + len(d))
    hi += 0x10000
    buf = bytearray(hi)
    for a, d in records(snap):
        buf[a:a + len(d)] = d

    cands = []
    if len(sys.argv) > 3 and not sys.argv[3].startswith("--"):
        for line in open(sys.argv[3], encoding="utf-8"):
            p = line.split()
            if len(p) >= 4 and p[0].replace(".", "").isdigit():
                try:
                    cands.append((int(p[2], 16), int(p[3])))
                except ValueError:
                    pass
    print("candidates: %d" % len(cands))

    results = []
    for pad in (0, 1, 2, 3):
        tpl = template(ppm, W, H, pad)
        flat = [tpl[r][c] for r in range(H) for c in range(W)]
        exp = sum(flat) / float(W * H)
        for base, stride in cands:
            need = W * H * stride
            if base + need > hi:
                continue
            vals = list(buf[base:base + need:stride])
            for name, v in (("id", vals),
                            ("T", [vals[c * W + r] for r in range(H) for c in range(W)])):
                acc, m1, m0 = score(v, flat)
                results.append((acc, exp, m1, m0, base, stride, pad, name))
    results.sort(key=lambda x: -x[0])
    print("explored share in template: %.3f" % (results[0][1] if results else 0))
    print("acc     m1     m0     base        stride pad  xform")
    seen = set()
    shown = 0
    for acc, exp, m1, m0, base, stride, pad, name in results:
        key = (base, stride)
        if key in seen:
            continue
        seen.add(key)
        print("  %.4f  %6.1f %6.1f  0x%08X  %-4d  %d   %s" % (acc, m1, m0, base, stride, pad, name))
        shown += 1
        if shown >= 40:
            break


if __name__ == "__main__":
    main()
