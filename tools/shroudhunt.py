# One-off dev helper: find the per-player shroud table.
#
# Context (2026-10-13): the map is 130x125 = 16 250 cells, the game has just
# started, so the table should be "one value almost everywhere, plus one small
# compact blob around the player's start".  That is a very sharp signature.
#
# Two passes:
#   A) every pointer reachable from the player's HouseClass
#   B) a global sweep: any 16 250-cell run at stride 1/2/4 dominated by two
#      values, ranked by how blob-like the minority is
#
#   python shroudhunt.py snapfile [--w 130] [--h 125] [--house 0x26AFF970]
#
# Read-only.
import sys
from collections import Counter

MAGIC = b"MOSNAP01"
MODULE_LO, MODULE_HI = 0x400000, 0xB93000


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


def blob_score(vals, W, H, minority_value):
    """Return (fill, contiguous_share, rows, cols) for the minority cells."""
    cells = [(i // W, i % W) for i in range(W * H) if vals[i] == minority_value]
    if not cells:
        return 0.0, 0.0, 0, 0
    rs = [c[0] for c in cells]; cs = [c[1] for c in cells]
    r0, r1, c0, c1 = min(rs), max(rs), min(cs), max(cs)
    box = (r1 - r0 + 1) * (c1 - c0 + 1)
    fill = len(cells) / float(box)
    s = set(cells)
    touch = 0
    for (r, c) in cells:
        if (r - 1, c) in s or (r + 1, c) in s or (r, c - 1) in s or (r, c + 1) in s:
            touch += 1
    return fill, touch / float(len(cells)), r1 - r0 + 1, c1 - c0 + 1


def evaluate(buf, hi, addr, W, H, stride):
    need = W * H * stride
    if addr < 0x10000 or addr + need > hi:
        return None
    vals = list(buf[addr:addr + need:stride])
    c = Counter(vals)
    if len(c) < 2 or len(c) > 8:
        return None
    top = c.most_common(2)
    share = (top[0][1] + top[1][1]) / float(W * H)
    if share < 0.90:
        return None
    minor = top[1][1] / float(W * H)
    if minor < 0.002 or minor > 0.45:
        return None
    fill, contig, br, bc = blob_score(vals, W, H, top[1][0])
    score = share * contig * (1.0 if 2 <= br <= H and 2 <= bc <= W else 0.0)
    return (score, share, minor, fill, contig, br, bc, top[0][0], top[1][0])


def main():
    snap = sys.argv[1]
    W = 130; H = 125
    if "--w" in sys.argv:
        W = int(sys.argv[sys.argv.index("--w") + 1])
    if "--h" in sys.argv:
        H = int(sys.argv[sys.argv.index("--h") + 1])
    house = None
    if "--house" in sys.argv:
        house = int(sys.argv[sys.argv.index("--house") + 1], 16)

    hi = 0
    spans = []
    for a, d in records(snap):
        spans.append((a, len(d))); hi = max(hi, a + len(d))
    hi += 0x10000
    buf = bytearray(hi)
    for a, d in records(snap):
        buf[a:a + len(d)] = d
    print("loaded %d MB, spans=%d, map %dx%d = %d cells"
          % (hi // 1048576, len(spans), W, H, W * H))
    i32 = lambda a: int.from_bytes(buf[a:a + 4], "little")

    # ---- Part A: pointers reachable from the house object ----
    if house:
        print()
        print("=== A) 从玩家势力档案 0x%08X 出发的指针 ===" % house)
        ptrs = {}
        for off in range(0, 0x6000, 4):
            v = i32(house + off)
            if 0x10000 <= v < 0x7F000000 and v not in ptrs:
                ptrs[v] = off
        print("  找到 %d 个不同的指针值" % len(ptrs))
        res = []
        for v, off in ptrs.items():
            for stride in (1, 2, 4):
                r = evaluate(buf, hi, v, W, H, stride)
                if r:
                    res.append((r[0], v, off, stride, r))
        res.sort(key=lambda x: -x[0])
        print("  其中像迷雾表的：%d 个" % len(res))
        for score, v, off, stride, r in res[:15]:
            print("    house+0x%03X -> 0x%08X  stride=%d  score=%.3f  两值%.3f 少数%.3f "
                  "填实%.2f 连通%.3f 包围盒%dx%d 值%02X/%02X"
                  % (off, v, stride, score, r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8]))

    # ---- Part B: global sweep ----
    print()
    print("=== B) 全内存扫描：16250 格、两种取值、少数方成团 ===")
    strides = (1, 2, 4)
    hits = []
    tested = 0
    for base, ln in spans:
        if ln < 8192:
            continue
        end = base + ln
        a = base
        while a < end:
            for stride in strides:
                need = W * H * stride
                if a + need > hi:
                    continue
                # 便宜的预筛：先看前 200 格
                pre = buf[a:a + 200 * stride:stride]
                cp = Counter(pre)
                tested += 1
                if len(cp) > 4:
                    continue
                if len(cp) < 2 or cp.most_common(2)[1][1] < 2:
                    continue
                r = evaluate(buf, hi, a, W, H, stride)
                if r:
                    hits.append((r[0], a, stride, r))
            a += 4096
    hits.sort(key=lambda x: -x[0])
    print("  粗筛 %d 个假设，命中 %d 个" % (tested, len(hits)))
    for score, a, stride, r in hits[:40]:
        print("    0x%08X stride=%d score=%.3f 两值%.3f 少数%.4f 填实%.2f 连通%.3f 包围盒%dx%d 值%02X/%02X"
              % (a, stride, score, r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8]))


if __name__ == "__main__":
    main()
