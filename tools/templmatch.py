# One-off dev helper: score memory against the player's minimap screenshot.
#
# 2026-10-13: the player explored a horizontal strip, so the "explored" mask is
# a band -- a far sharper signature than the fuzzy blob of a fresh game.
#
# The minimap screenshot has a UI frame around it; the frame box is given on the
# command line so only the map area is sampled.
#
#   python templmatch.py snapfile mm.ppm --w 130 --h 125
#        --box x0,y0,x1,y1        (map area inside the screenshot, inclusive)
#        [--strides 1,2,4] [--transforms id,T]
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


def build_template(ppm, W, H, box):
    iw, ih, px = load_ppm(ppm)
    x0, y0, x1, y1 = box
    bw = x1 - x0 + 1
    bh = y1 - y0 + 1
    tpl = []
    for r in range(H):
        sy = y0 + (2 * r + 1) * bh // (2 * H)
        for c in range(W):
            sx = x0 + (2 * c + 1) * bw // (2 * W)
            k = (sy * iw + sx) * 3
            tpl.append(0 if (px[k] == 0 and px[k + 1] == 0 and px[k + 2] == 0) else 1)
    return tpl


def transpose(tpl, W, H):
    """同一个数组按「宽 = H」重新解读（等价于转置，但对非方阵才成立）。"""
    return [tpl[r * W + c] for c in range(W) for r in range(H)]


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
    strides = [1]
    if "--strides" in sys.argv:
        strides = [int(v) for v in sys.argv[sys.argv.index("--strides") + 1].split(",")]

    tpl = build_template(ppm, W, H, box)
    tplT = transpose(tpl, W, H)
    exp = sum(tpl) / float(W * H)
    print("template %dx%d  explored share %.3f  box=%s" % (W, H, exp, box))

    hi = 0
    spans = []
    for a, d in records(snap):
        spans.append((a, len(d))); hi = max(hi, a + len(d))
    hi += 0x10000
    buf = bytearray(hi)
    for a, d in records(snap):
        buf[a:a + len(d)] = d
    print("loaded %d MB, spans=%d" % (hi // 1048576, len(spans)))

    n = W * H
    cand = []
    tested = 0
    for base, ln in spans:
        if ln < 8192:
            continue
        a = base
        end = base + ln
        while a < end - n:
            for s in strides:
                if a + n * s > hi:
                    continue
                tested += 1
                pre = buf[a:a + 300 * s:s]
                cp = Counter(pre)
                if len(cp) > 3:
                    continue
                cand.append((a, s))
            a += 4096
    print("prefilter: %d hypotheses -> %d candidates" % (tested, len(cand)))

    # 把模板转成 0/1 字节串，对照也用 translate 变成 0/1 字节串，
    # 再用"大整数异或 + count(1)"算汉明距离 —— 全是 C 层操作，快得多。
    tpl_bytes_id = bytes(tpl)
    tpl_bytes_T = bytes(tplT)
    tpl_int_id = int.from_bytes(tpl_bytes_id, "big")
    tpl_int_T = int.from_bytes(tpl_bytes_T, "big")

    hits = []
    for a, s in cand:
        vals = buf[a:a + n * s:s]
        c = Counter(vals)
        if len(c) < 2 or len(c) > 6:
            continue
        top = c.most_common(2)
        share = (top[0][1] + top[1][1]) / float(n)
        if share < 0.90:
            continue
        v1 = top[0][0]
        table = bytearray(256)
        table[v1] = 1
        pred = vals.translate(bytes(table))
        pred_int = int.from_bytes(pred, "big")
        for name, ti in (("id", tpl_int_id), ("T", tpl_int_T)):
            diff = (pred_int ^ ti).to_bytes(n, "big").count(1)
            acc = (n - diff) / float(n)
            if acc < 0.5:
                acc = 1.0 - acc
            hits.append((acc, a, s, name, share, top[1][1] / float(n), v1, top[1][0]))
    hits.sort(key=lambda x: -x[0])
    print()
    print("acc     base        stride xform 两值   少数    值")
    seen = set()
    shown = 0
    for acc, a, s, name, share, minor, v1, v2 in hits:
        if (a, s) in seen:
            continue
        seen.add((a, s))
        print("  %.4f  0x%08X  %-2d    %-4s  %.3f  %.4f  %02X/%02X" % (acc, a, s, name, share, minor, v1, v2))
        shown += 1
        if shown >= 30:
            break


if __name__ == "__main__":
    main()
