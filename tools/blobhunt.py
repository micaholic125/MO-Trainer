# One-off dev helper (2026-10-13): find the player's shroud table by SHAPE.
#
# The map is 130x125 = 16 250 cells and the game has just started, so the table
# should be "one value almost everywhere + one compact connected blob".  Two
# storage hypotheses are swept over the whole snapshot:
#
#   A) one byte per cell  -> a 16 250-byte run
#   B) one bit per cell   -> a 2 031-byte run
#
# Ranking uses connected components, which the earlier "has a neighbour" score
# could not distinguish (a thin diagonal line scored 1.0 there).
#
#   python blobhunt.py snapfile [--w 130] [--h 125] [--maxcomp 8]
#
# Read-only.
import sys
from collections import Counter, deque

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


def analyse(ones, W, H):
    """ones = sorted list of flat indices that are 'explored'."""
    n = len(ones)
    if n == 0:
        return None
    rs = [i // W for i in ones]
    cs = [i % W for i in ones]
    r0, r1, c0, c1 = min(rs), max(rs), min(cs), max(cs)
    box = (r1 - r0 + 1) * (c1 - c0 + 1)
    fill = n / float(box)
    s = set(ones)
    seen = set()
    comps = 0
    biggest = 0
    for i in ones:
        if i in seen:
            continue
        comps += 1
        size = 0
        dq = deque([i]); seen.add(i)
        while dq:
            j = dq.popleft(); size += 1
            r, c = j // W, j % W
            for nb in ((r - 1) * W + c if r > 0 else -1,
                       (r + 1) * W + c if r < H - 1 else -1,
                       j - 1 if c > 0 else -1,
                       j + 1 if c < W - 1 else -1):
                if nb >= 0 and nb in s and nb not in seen:
                    seen.add(nb); dq.append(nb)
        if size > biggest:
            biggest = size
    return dict(n=n, box=box, fill=fill, comps=comps, biggest=biggest,
                bbox="%dx%d" % (r1 - r0 + 1, c1 - c0 + 1),
                major=biggest / float(n))


def byte_probe(buf, hi, addr, W, H, maxcomp):
    need = W * H
    if addr + need > hi:
        return None
    vals = buf[addr:addr + need]
    c = Counter(vals)
    if len(c) < 2 or len(c) > 6:
        return None
    top = c.most_common(2)
    share = (top[0][1] + top[1][1]) / float(W * H)
    if share < 0.90:
        return None
    minor = top[1][1] / float(W * H)
    if minor < 0.001 or minor > 0.5:
        return None
    ones = [i for i in range(W * H) if vals[i] == top[1][0]]
    a = analyse(ones, W, H)
    if a is None or a["comps"] > maxcomp:
        return None
    score = share * a["major"]
    return dict(score=score, share=share, minor=minor, **a)


def bit_probe(buf, hi, addr, W, H, maxcomp, order):
    need = (W * H + 7) // 8
    if addr + need > hi:
        return None
    blob = buf[addr:addr + need]
    ones = []
    for i in range(W * H):
        b = blob[i >> 3]
        if order == 0:
            bit = (b >> (i & 7)) & 1
        else:
            bit = (b >> (7 - (i & 7))) & 1
        if bit:
            ones.append(i)
    n = len(ones)
    if n < W * H // 200 or n > W * H // 2:
        return None
    a = analyse(ones, W, H)
    if a is None or a["comps"] > maxcomp:
        return None
    return dict(score=a["major"], share=1.0, minor=n / float(W * H), **a)


def main():
    snap = sys.argv[1]
    W, H = 130, 125
    if "--w" in sys.argv:
        W = int(sys.argv[sys.argv.index("--w") + 1])
    if "--h" in sys.argv:
        H = int(sys.argv[sys.argv.index("--h") + 1])
    maxcomp = 8
    if "--maxcomp" in sys.argv:
        maxcomp = int(sys.argv[sys.argv.index("--maxcomp") + 1])

    hi = 0
    spans = []
    for a, d in records(snap):
        spans.append((a, len(d))); hi = max(hi, a + len(d))
    hi += 0x10000
    buf = bytearray(hi)
    for a, d in records(snap):
        buf[a:a + len(d)] = d
    print("loaded %d MB, spans=%d, map %dx%d" % (hi // 1048576, len(spans), W, H))

    hitsA, hitsB = [], []
    testedA = testedB = 0
    for base, ln in spans:
        if ln < 4096:
            continue
        a = base
        end = base + ln
        while a < end - W * H:
            testedA += 1
            r = byte_probe(buf, hi, a, W, H, maxcomp)
            if r:
                hitsA.append((r["score"], a, r))
            a += 4096
    print("A) 一格一字节：粗扫 %d 个起点，命中 %d" % (testedA, len(hitsA)))

    for base, ln in spans:
        if ln < 4096:
            continue
        a = base
        end = base + ln
        while a < end - (W * H) // 8:
            for order in (0, 1):
                testedB += 1
                r = bit_probe(buf, hi, a, W, H, maxcomp, order)
                if r:
                    hitsB.append((r["score"], a, order, r))
            a += 4096
    print("B) 一格一位：粗扫 %d 个起点，命中 %d" % (testedB, len(hitsB)))

    hitsA.sort(key=lambda x: -x[0])
    hitsB.sort(key=lambda x: -x[0])
    print()
    print("=== A) 一格一字节 前 20 ===")
    for score, a, r in hitsA[:20]:
        print("  0x%08X score=%.3f 两值%.3f 少数%.4f 块数%d 主块占比%.2f 包围盒%s 填实%.2f"
              % (a, score, r["share"], r["minor"], r["comps"], r["major"], r["bbox"], r["fill"]))
    print()
    print("=== B) 一格一位 前 20 ===")
    for score, a, order, r in hitsB[:20]:
        print("  0x%08X order=%d score=%.3f 少数%.4f 块数%d 主块占比%.2f 包围盒%s 填实%.2f"
              % (a, order, score, r["minor"], r["comps"], r["major"], r["bbox"], r["fill"]))


if __name__ == "__main__":
    main()
