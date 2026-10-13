# One-off dev helper: the last untested storage shape -- a dedicated
# one-bit-per-cell shroud table (16 250 bits = 2 031 bytes), swept over the
# whole snapshot and scored against the minimap template.
#
# Cheap prefilter: the number of set bits in the window must be in the same
# ballpark as the explored share (~11%), which kills the vast majority of
# windows (random data is ~50%, zero pages are 0%).
#
#   python bitpackhunt.py snapfile mm.ppm [--w 130] [--h 125] [--box ...] [--step 256]
#
# Read-only.
import sys

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
    f = []
    while len(f) < 4:
        while raw[pos:pos + 1].isspace():
            pos += 1
        s = pos
        while not raw[pos:pos + 1].isspace():
            pos += 1
        f.append(raw[s:pos])
    pos += 1
    return int(f[1]), int(f[2]), raw[pos:]


def build(ppm, W, H, box):
    iw, ih, px = load_ppm(ppm)
    x0, y0, x1, y1 = box
    bw = x1 - x0 + 1; bh = y1 - y0 + 1
    out = []
    for r in range(H):
        sy = y0 + (2 * r + 1) * bh // (2 * H)
        for c in range(W):
            sx = x0 + (2 * c + 1) * bw // (2 * W)
            k = (sy * iw + sx) * 3
            out.append(0 if (px[k] == 0 and px[k + 1] == 0 and px[k + 2] == 0) else 1)
    return out


def main():
    snap = sys.argv[1]; ppm = sys.argv[2]
    W, H = 130, 125
    if "--w" in sys.argv:
        W = int(sys.argv[sys.argv.index("--w") + 1])
    if "--h" in sys.argv:
        H = int(sys.argv[sys.argv.index("--h") + 1])
    box = (6, 7, 118, 114)
    if "--box" in sys.argv:
        box = tuple(int(v) for v in sys.argv[sys.argv.index("--box") + 1].split(","))
    step = 256
    if "--step" in sys.argv:
        step = int(sys.argv[sys.argv.index("--step") + 1])

    n = W * H
    nbytes = (n + 7) // 8
    tpl = build(ppm, W, H, box)
    share = sum(tpl) / float(n)
    print("map %dx%d = %d cells, bit table = %d bytes, template explored %.4f"
          % (W, H, n, nbytes, share))
    ti = int.from_bytes(bytes(tpl), "big")

    hi = 0
    spans = []
    for a, d in records(snap):
        spans.append((a, len(d))); hi = max(hi, a + len(d))
    hi += 0x10000
    buf = bytearray(hi)
    for a, d in records(snap):
        buf[a:a + len(d)] = d
    print("loaded %d MB" % (hi // 1048576))

    lo = int(n * (share * 0.4))
    hi_bits = int(n * min(0.6, share * 2.5 + 0.05))
    print("prefilter: set bits must be in [%d, %d]" % (lo, hi_bits))

    cand = []
    tested = 0
    for base, ln in spans:
        if ln < nbytes + 16:
            continue
        a = base
        end = base + ln - nbytes
        while a < end:
            tested += 1
            v = int.from_bytes(buf[a:a + nbytes], "big")
            bc = v.bit_count()
            if lo <= bc <= hi_bits:
                cand.append((a, bc))
            a += step
    print("tested %d starts -> %d candidates" % (tested, len(cand)))

    res = []
    for a, bc in cand:
        blob = buf[a:a + nbytes]
        for order in (0, 1):
            if order == 0:
                bits = 0
                for i in range(nbytes):
                    bits = (bits << 8) | blob[i]
                # 展开：bit i 取 byte i//8 的第 (i&7) 位
                pred = bytearray(n)
                for i in range(n):
                    pred[i] = (blob[i >> 3] >> (i & 7)) & 1
            else:
                pred = bytearray(n)
                for i in range(n):
                    pred[i] = (blob[i >> 3] >> (7 - (i & 7))) & 1
            pi = int.from_bytes(bytes(pred), "big")
            d1 = (pi ^ ti).to_bytes(n, "big").count(1)
            acc = (n - d1) / float(n)
            if acc < 0.5:
                acc = 1.0 - acc
            res.append((acc, a, order, bc))
    res.sort(key=lambda x: -x[0])
    print()
    print("baseline (all-unexplored) = %.4f" % (1.0 - share))
    print("acc     base        bitorder  setbits")
    for acc, a, order, bc in res[:25]:
        print("  %.4f  0x%08X  %d         %d" % (acc, a, order, bc))


if __name__ == "__main__":
    main()
