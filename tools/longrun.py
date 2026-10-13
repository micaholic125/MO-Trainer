# One-off dev helper: diff two MemSnap snapshots and look for LONG RUNS of
# consecutive changed bytes.
#
# Why this shape (and why it is different from the earlier "changed exactly once"
# hunts that all came up empty):
#
#   * scattered noise  -- timers, counters, the radar buffer -- changes isolated
#     bytes all over the place.  A run of 20+ CONSECUTIVE changed bytes is not
#     something scattered noise produces.
#   * a shroud buffer being revealed changes a CONTIGUOUS block of cells (the
#     piece of map the player just walked), so if it is stored one byte per cell
#     it MUST show up as a long unbroken run.
#
# So: runs, not strides.  And report the before/after values, because a real
# shroud flips between two fixed values (e.g. 0xFF -> 0x00) rather than to
# arbitrary new numbers.
#
#   python longrun.py a.snap b.snap [--min 16] [--top 40]
#
# Read-only.
import mmap
import struct
import sys

MAGIC = b"MOSNAP01"
PAGE = 4096
ZERO = bytes(PAGE)


def index_records(path):
    fh = open(path, "rb")
    mm = mmap.mmap(fh.fileno(), 0, access=mmap.ACCESS_READ)
    if mm[:8] != MAGIC:
        raise SystemExit("%s: bad magic" % path)
    pos = 8
    pos += 4
    stamp_len = struct.unpack_from("<i", mm, pos)[0]
    pos += 4
    stamp = mm[pos:pos + stamp_len].decode("utf-8", "replace")
    pos += stamp_len
    recs = []
    regions = []
    while pos < len(mm):
        kind = mm[pos]
        pos += 1
        if kind == 0:
            break
        if kind == 1:
            base, size, protect, typ = struct.unpack_from("<QQII", mm, pos)
            regions.append((base, size, protect, typ))
            pos += 24
            continue
        if kind == 2:
            addr, ln = struct.unpack_from("<II", mm, pos)
            pos += 8
            recs.append((addr, pos, ln))
            pos += ln
            continue
        raise SystemExit("%s: bad record kind %d" % (path, kind))
    return fh, mm, stamp, recs, regions


def build_pages(mm, recs):
    pages = {}
    for addr, off, ln in recs:
        p = addr & ~(PAGE - 1)
        head = addr - p
        i = 0
        while i < ln:
            take = PAGE - head
            if take > ln - i:
                take = ln - i
            buf = pages.get(p)
            if buf is None:
                buf = bytearray(PAGE)
                pages[p] = buf
            buf[head:head + take] = mm[off + i:off + i + take]
            i += take
            p += PAGE
            head = 0
    return pages


def runs_of_changed(a, b):
    """Yield (start_offset, length) for each maximal run of differing bytes."""
    n = len(a)
    i = 0
    while i < n:
        if a[i] == b[i]:
            i += 1
            continue
        j = i
        while j < n and a[j] != b[j]:
            j += 1
        yield i, j - i
        i = j


def main():
    args = [x for x in sys.argv[1:] if not x.startswith("--")]
    if len(args) < 2:
        raise SystemExit(__doc__)
    minimum = 16
    if "--min" in sys.argv:
        minimum = int(sys.argv[sys.argv.index("--min") + 1])
    top = 40
    if "--top" in sys.argv:
        top = int(sys.argv[sys.argv.index("--top") + 1])

    # --require-before 0xFF keeps only runs that overwrote a uniform block of that
    # byte. "Unexplored" is stored as one repeated value, so a strip of map being
    # revealed must show up as a uniform block of it being replaced. Without this,
    # the report is swamped by zeroed memory being filled for the first time, which
    # looks identical in every other respect.
    require_before = None
    if "--require-before" in sys.argv:
        require_before = int(sys.argv[sys.argv.index("--require-before") + 1], 16)

    fh_a, mm_a, stamp_a, recs_a, regs_a = index_records(args[0])
    fh_b, mm_b, stamp_b, recs_b, regs_b = index_records(args[1])
    print("A: %s  regions=%d" % (stamp_a, len(regs_a)))
    print("B: %s  regions=%d" % (stamp_b, len(regs_b)))

    # region type/protect map, so a hit can be described
    regmap = {}
    for base, size, protect, typ in regs_b:
        regmap[base] = (size, protect, typ)

    pa = build_pages(mm_a, recs_a)
    pb = build_pages(mm_b, recs_b)
    print("pages A=%d B=%d" % (len(pa), len(pb)))

    hits = []
    for p in sorted(set(pa) | set(pb)):
        a = pa.get(p, ZERO)
        b = pb.get(p, ZERO)
        if a == b:
            continue
        for off, ln in runs_of_changed(a, b):
            if ln < minimum:
                continue
            addr = p + off
            before = a[off]
            after = b[off]
            # how uniform is the run? a real shroud flips one value to another
            same_before = all(a[off + k] == before for k in range(ln))
            same_after = all(b[off + k] == after for k in range(ln))
            # Three grades, not one:
            #   both  -- uniform before AND after (a straight swap of one value)
            #   wipe  -- uniform BEFORE, mixed after: exactly what overwriting an
            #            untouched "all unexplored" strip with real terrain looks like
            #   other -- neither
            if same_before and same_after:
                grade = "both"
            elif same_before:
                grade = "wipe"
            elif same_after:
                grade = "fill"
            else:
                grade = "other"
            # "spare": does the run still have a NEIGHBOUR holding the old value in
            # snapshot B? A revealed strip of map is surrounded by map that is still
            # unexplored, so the answer is yes. A block that was freshly allocated and
            # then zeroed has no such survivor -- the whole allocation goes at once.
            # This is what separates the two, which look identical otherwise.
            spare = False
            if off > 0 and b[off - 1] == before:
                spare = True
            if off + ln < len(b) and b[off + ln] == before:
                spare = True

            hits.append((ln, addr, before, after, grade,
                         a[off:off + min(ln, 8)].hex(), b[off:off + min(ln, 8)].hex(),
                         spare))

    # per-region histogram, so the handful of repainting surfaces can be named and
    # set aside instead of drowning everything else
    if "--by-region" in sys.argv:
        byreg = {}
        for ln, addr, before, after, flat, ba, bb, spare in hits:
            base = None
            for rbase in regmap:
                if rbase <= addr < rbase + regmap[rbase][0]:
                    base = rbase
                    break
            key = base if base is not None else addr
            e = byreg.setdefault(key, [0, 0, 0])
            e[0] += 1
            e[1] += ln
            if flat:
                e[2] += 1
        rows = sorted(byreg.items(), key=lambda kv: -kv[1][0])
        print()
        print("=== runs grouped by memory region (top 25) ===")
        print("%-12s %-8s %-14s %-6s %s" % ("region", "runs", "run-bytes", "flat", "size / protect / type"))
        for base, (n, tot, fl) in rows[:25]:
            info = regmap.get(base)
            desc = ("size=0x%X prot=0x%X type=0x%X" % (info[0], info[1], info[2])) if info else "(unknown)"
            print("0x%08X   %-8d %-14d %-6d %s" % (base, n, tot, fl, desc))

    if require_before is not None:
        hits = [h for h in hits if h[2] == require_before]
        print()
        print("filtered to runs whose BEFORE value is 0x%02X: %d remain" % (require_before, len(hits)))

    flat_hits = [h for h in hits if h[4] in ("both", "wipe") and h[7]]
    rest = [h for h in hits if not (h[4] in ("both", "wipe") and h[7])]
    flat_hits.sort(key=lambda h: -h[0])
    rest.sort(key=lambda h: -h[0])
    hits = flat_hits + rest

    print()
    print("runs of >= %d consecutive changed bytes: %d   (of which FLAT: %d)"
          % (minimum, len(hits), len(flat_hits)))
    print()
    print("FLAT means every byte in the run held the SAME value before and the SAME")
    print("value after -- which is what a revealed strip of a shroud table looks like.")
    print("A repainted rendering surface is never flat, which is why this is the")
    print("discriminator that finally makes the report readable.")
    print()
    print("%-7s %-10s %-22s %-6s %s" % ("len", "address", "before -> after", "flat", "first 8 bytes"))
    for ln, addr, before, after, flat, ba, bb, spare in hits[:top]:
        base = None
        for rbase in regmap:
            if rbase <= addr < rbase + regmap[rbase][0]:
                base = rbase
                break
        extra = ""
        if base is not None:
            size, protect, typ = regmap[base]
            extra = "  [region 0x%08X size=0x%X prot=0x%X type=0x%X]" % (base, size, protect, typ)
        print("%-7d 0x%08X 0x%02X -> 0x%02X%s %-6s %s | %s"
              % (ln, addr, before, after, " " * 8, "yes" if flat else "no", ba, bb))
        print("        %s" % extra)

    if hits:
        print()
        print("longest: %d bytes at 0x%08X" % (hits[0][0], hits[0][1]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
