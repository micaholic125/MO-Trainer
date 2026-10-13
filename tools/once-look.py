# One-off dev helper: after a rapid snapshot burst, look at the addresses that
# changed EXACTLY ONCE and ask "is there a hidden table in here?".
#
# The shroud table, if it exists, must show two things that scattered noise
# cannot fake at the same time:
#   * a regular stride  -- its cells are fixed-size records, so the changed
#     addresses all sit at the same offset inside a repeating period;
#   * a 2-D shape       -- laid out at the map's width, the changed cells form
#     the strip the player just walked, not a scatter.
#
#   python once-look.py once.txt [--w 61] [--h 87] [--top 12]
#
# Read-only.
import sys
from collections import Counter, deque

MAGIC = b""   # not needed: this reads the text dump written by snapseq.py


def load(path):
    addrs = []
    for line in open(path, encoding="utf-8"):
        if line.startswith("#"):
            continue
        p = line.split()
        if len(p) >= 3:
            addrs.append((int(p[0], 16), int(p[1], 16), int(p[2], 16)))
    return addrs


def best_phase(addrs, stride):
    """For a fixed stride, which residue holds the most addresses?"""
    c = Counter(a % stride for a, _o, _n in addrs)
    r, n = c.most_common(1)[0]
    return r, n


def analyse_stream(addrs, W, H, stride, residue):
    """Render the phased addresses as a W-wide grid."""
    idx = sorted((a - residue) // stride for a, _o, _n in addrs if a % stride == residue)
    idx = [i for i in idx if 0 <= i < W * H]
    if not idx:
        return None
    s = set(idx)
    rs = [i // W for i in idx]; cs = [i % W for i in idx]
    r0, r1, c0, c1 = min(rs), max(rs), min(cs), max(cs)
    box = (r1 - r0 + 1) * (c1 - c0 + 1)
    fill = len(idx) / float(box)
    seen = set(); comps = 0
    for i in idx:
        if i in seen:
            continue
        comps += 1
        dq = deque([i]); seen.add(i)
        while dq:
            j = dq.popleft()
            r, c = j // W, j % W
            for nb in ((r - 1) * W + c if r > 0 else -1,
                       (r + 1) * W + c if r < H - 1 else -1,
                       j - 1 if c > 0 else -1,
                       j + 1 if c < W - 1 else -1):
                if nb >= 0 and nb in s and nb not in seen:
                    seen.add(nb); dq.append(nb)
    return dict(n=len(idx), fill=fill, comps=comps,
                bbox="rows %d..%d cols %d..%d" % (r0, r1, c0, c1),
                grid=(r0, r1, c0, c1))


def main():
    path = sys.argv[1]
    W, H = 61, 87
    if "--w" in sys.argv:
        W = int(sys.argv[sys.argv.index("--w") + 1])
    if "--h" in sys.argv:
        H = int(sys.argv[sys.argv.index("--h") + 1])
    top = 12
    if "--top" in sys.argv:
        top = int(sys.argv[sys.argv.index("--top") + 1])

    addrs = load(path)
    print("changed-exactly-once entries: %d" % len(addrs))
    if not addrs:
        return
    base = len(addrs)

    strides = [1, 2, 3, 4, 6, 8, 12, 16, 20, 24, 32, 36, 40, 48, 64, 96, 128, 256]
    rows = []
    for s in strides:
        r, n = best_phase(addrs, s)
        rows.append((n / float(base), s, r, n))
    rows.sort(key=lambda x: -x[0])
    print()
    print("dominant stride (a real table shows up as a high share on ONE stride):")
    for share, s, r, n in rows[:10]:
        print("   stride %-4d residue %-4d  %6d / %d  = %.3f" % (s, r, n, base, share))

    print()
    print("shape check for the top strides (laid out at width %d):" % W)
    shown = 0
    for share, s, r, n in rows:
        if share < 0.05:
            continue
        a = analyse_stream(addrs, W, H, s, r)
        if a is None:
            continue
        print("   stride %-4d residue %-4d  n=%-7d blocks=%-6d fill=%.2f  %s"
              % (s, r, a["n"], a["comps"], a["fill"], a["bbox"]))
        shown += 1
        if shown >= top:
            break


if __name__ == "__main__":
    main()
