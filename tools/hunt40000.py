# One-off dev helper: the map is 200x200, so the shroud field has exactly 40 000
# cells.  Search a MemSnap snapshot for a 40 000-cell run at some stride whose
# values are dominated by two distinct bytes -- the signature of an
# explored/unexplored map array.
#
# Two-stage so it finishes in seconds rather than an hour:
#   stage 1 -- look at only the first 160 strided values of each candidate
#   stage 2 -- full 40 000-cell check on the survivors, then rank
#
#   python hunt40000.py snapfile [--cells 40000] [--strides 1,2,4,8,16,32,64]
#
# Read-only.
import sys
from collections import Counter

MAGIC = b"MOSNAP01"

PRE = 160          # samples used by the cheap first pass


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


def main():
    path = sys.argv[1]
    cells = 40000
    if "--cells" in sys.argv:
        cells = int(sys.argv[sys.argv.index("--cells") + 1])
    strides = [1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256]
    if "--strides" in sys.argv:
        strides = [int(x) for x in sys.argv[sys.argv.index("--strides") + 1].split(",")]

    hi = 0
    spans = []
    for a, data in records(path):
        spans.append((a, len(data)))
        if a + len(data) > hi:
            hi = a + len(data)
    hi += 0x10000
    print("flat address space: %d MB" % (hi // 1048576))
    buf = bytearray(hi)
    for a, data in records(path):
        buf[a:a + len(data)] = data
    print("loaded, %d spans" % len(spans))

    cand = []
    stage1 = 0
    for base, ln in spans:
        if ln < 8192:
            continue
        end = base + ln
        a = base
        while a < end:
            for s in strides:
                need = cells * s
                if a + need > hi:
                    continue
                stage1 += 1
                pre = buf[a:a + PRE * s:s]
                c = Counter(pre)
                if len(c) > 3:
                    continue
                top = c.most_common(2)
                if len(top) < 2 or top[1][1] < 3:
                    continue
                cand.append((a, s))
            a += 4096
    print("stage 1: %d hypotheses -> %d candidates" % (stage1, len(cand)))

    hits = []
    for a, s in cand:
        need = cells * s
        vals = buf[a:a + need:s]
        c = Counter(vals)
        if len(c) > 6:
            continue
        top = c.most_common(2)
        if len(top) < 2:
            continue
        share = (top[0][1] + top[1][1]) / float(cells)
        minor = top[1][1] / float(cells)
        if share < 0.97 or minor < 0.03:
            continue
        hits.append((share, minor, a, s, top[0][0], top[1][0], len(c)))

    hits.sort(key=lambda h: -(h[0] * 100 + h[1]))
    print("stage 2 hits: %d" % len(hits))
    print("share  minor  address     stride  valA valB distinct")
    for share, minor, a, s, va, vb, nd in hits[:80]:
        print("  %.4f %.3f  0x%08X  %-4d  %02X   %02X   %d" % (share, minor, a, s, va, vb, nd))


if __name__ == "__main__":
    main()
