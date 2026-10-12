# One-off dev helper: try every single BIT of every byte inside the 36-byte map
# cell as "this bit = the cell is explored".  A one-bit-per-cell shroud hides
# inside a flags byte, so a whole-byte comparison can never see it.
#
#   python bit-score.py snapfile mm.ppm [--w 200] [--h 200]
#
# Read-only.
import sys

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

    hi = 0
    for a, d in records(snap):
        hi = max(hi, a + len(d))
    hi += 0x10000
    buf = bytearray(hi)
    for a, d in records(snap):
        buf[a:a + len(d)] = d

    iw, ih, px = load_ppm(ppm)
    # 预先把模板每个像素的"已探索"取出来（矩形取样），再拼成 200x200
    tpl_rect = []
    for r in range(H):
        sy = (2 * r + 1) * ih // (2 * H)
        for c in range(W):
            sx = (2 * c + 1) * iw // (2 * W)
            k = (sy * iw + sx) * 3
            tpl_rect.append(0 if (px[k] == 0 and px[k + 1] == 0 and px[k + 2] == 0) else 1)

    arrays = [(0x21810024, 40000, 200, 200), (0x1EB24E24, 10000, 100, 100)]
    results = []
    for base, n, AW, AH in arrays:
        if base + n * CELL > hi:
            continue
        bytes_ = buf[base:base + n * CELL]
        for off in range(CELL):
            col = bytes_[off::CELL]
            for bit in range(8):
                on = 0; tot1 = 0; tot0 = 0; c11 = 0; c10 = 0
                for i in range(n):
                    b = (col[i] >> bit) & 1
                    t = tpl_rect[i] if (AW == W and AH == H) else None
                    if t is None:
                        # 低分辨率数组：按其自身网格取样
                        rr = (i // AW) * H // AH
                        cc = (i % AW) * W // AW
                        t = tpl_rect[rr * W + cc]
                    if t:
                        tot1 += 1
                        if b: c11 += 1
                    else:
                        tot0 += 1
                        if b: c10 += 1
                if tot1 == 0 or tot0 == 0:
                    continue
                # 两种极性都算
                a1 = (c11 + (tot0 - c10)) / float(n)      # bit=1 <=> explored
                a2 = ((tot1 - c11) + c10) / float(n)      # bit=0 <=> explored
                acc = a1 if a1 > a2 else a2
                results.append((acc, base, off, bit, c11 / float(tot1), c10 / float(tot0)))
    results.sort(key=lambda x: -x[0])
    print("acc     base        offset bit   P(bit=1|explored)  P(bit=1|unexplored)")
    for acc, base, off, bit, p1, p0 in results[:25]:
        print("  %.4f  0x%08X  +0x%02X  %d      %.3f              %.3f"
              % (acc, base, off, bit, p1, p0))


if __name__ == "__main__":
    main()
