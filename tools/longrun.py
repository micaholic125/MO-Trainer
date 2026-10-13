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
            hits.append((ln, addr, before, after, same_before and same_after,
                         a[off:off + min(ln, 8)].hex(), b[off:off + min(ln, 8)].hex()))

    hits.sort(key=lambda h: -h[0])
    print()
    print("runs of >= %d consecutive changed bytes: %d" % (minimum, len(hits)))
    print()
    print("%-7s %-10s %-22s %-6s %s" % ("len", "address", "before -> after", "flat", "first 8 bytes"))
    for ln, addr, before, after, flat, ba, bb in hits[:top]:
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
