# One-off dev helper: find the bytes that changed EXACTLY ONCE across a sequence
# of MemSnap snapshots (and then stayed put).
#
# Why: a memory dump of a running game churns millions of bytes per second
# (radar buffer, timers, sound).  But a shroud cell that gets revealed flips
# once, from "unexplored" to "explored", and then never moves again.
# "Changed exactly once" is therefore a very sharp filter.
#
# Normalisation: region bases are only 4 KB aligned, so 64 KB blocks from two
# snapshots can overlap.  Everything here is re-cut into 4 KB pages, which are
# unique per snapshot, so an address line-up between snapshots is well defined.
#
#   python snapseq.py w1.snap w2.snap ... [--out once.txt] [--top 40]
#
# Read-only.
import mmap
import re
import sys

MAGIC = b"MOSNAP01"
PAGE = 4096
ZERO = bytes(PAGE)
NONZERO = re.compile(rb"[^\x00]")


def index_records(path):
    """Read only the header/record table; data stays in the mapped file."""
    fh = open(path, "rb")
    mm = mmap.mmap(fh.fileno(), 0, access=mmap.ACCESS_READ)
    if mm[:8] != MAGIC:
        raise SystemExit("%s: bad magic" % path)
    pos = 8
    pos += 4                                     # block size
    stamp_len = int.from_bytes(mm[pos:pos + 4], "little"); pos += 4
    stamp = mm[pos:pos + stamp_len].decode("utf-8", "replace"); pos += stamp_len
    recs = []
    while pos < len(mm):
        kind = mm[pos]; pos += 1
        if kind == 0:
            break
        if kind == 1:
            pos += 24
            continue
        if kind == 2:
            addr = int.from_bytes(mm[pos:pos + 4], "little"); pos += 4
            ln = int.from_bytes(mm[pos:pos + 4], "little"); pos += 4
            recs.append((addr, pos, ln))
            pos += ln
            continue
        raise SystemExit("%s: bad record kind %d" % (path, kind))
    return fh, mm, stamp, recs


def build_pages(mm, recs):
    """Cut every record into 4 KB pages keyed by page base address."""
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


def clusters(addrs, gap=64):
    runs = []
    start = prev = None
    count = 0
    for a in addrs:
        if start is None:
            start = prev = a; count = 1
        elif a - prev <= gap:
            prev = a; count += 1
        else:
            runs.append((start, prev, count)); start = prev = a; count = 1
    if start is not None:
        runs.append((start, prev, count))
    runs.sort(key=lambda r: -r[2])
    return runs


def main():
    # 带值的选项，值本身不是文件名（曾经把 --exclude 的值当成快照文件名，报错了）
    OPTS = ("--exclude", "--out", "--top")
    files = []
    skip = False
    for a in sys.argv[1:]:
        if skip:
            skip = False
            continue
        if a in OPTS:
            skip = True
            continue
        if a.startswith("--"):
            continue
        files.append(a)
    if len(files) < 2:
        raise SystemExit(__doc__)
    out_path = "once.txt"
    if "--out" in sys.argv:
        out_path = sys.argv[sys.argv.index("--out") + 1]
    top = 40
    if "--top" in sys.argv:
        top = int(sys.argv[sys.argv.index("--top") + 1])

    # 2026-10-13：空闲实测发现，噪音的 98.5% 集中在游戏的**画面缓冲**
    # 0x0B500000..0x0B850000（大块重绘、密度 0.96~1.00）。把这一段整个跳过，
    # 「只变过一次」的噪音从 175 万直接掉到 2.6 万 —— 这才让信号有机会露头。
    excl = []
    if "--exclude" in sys.argv:
        for spec in sys.argv[sys.argv.index("--exclude") + 1].split(","):
            a, b = spec.split("-")
            excl.append((int(a, 16), int(b, 16)))
        print("excluding %d ranges: %s"
              % (len(excl), ", ".join("0x%08X-0x%08X" % e for e in excl)))

    once = {}       # page -> bytearray of transition counts (capped at 3)
    when = {}       # page -> bytearray of "which gap" for the single transition
    before = {}     # page -> bytearray of the value before that transition
    after = {}      # page -> bytearray of the value after that transition

    prev = None
    stamps = []
    for i, path in enumerate(files):
        fh, mm, stamp, recs = index_records(path)
        cur = build_pages(mm, recs)
        stamps.append(stamp)
        print("[%2d] %s  pages=%d" % (i + 1, stamp, len(cur)))
        if prev is not None:
            hot = 0
            changed_bytes = 0
            for p in sorted(set(prev) | set(cur)):
                if any(lo <= p < hi for lo, hi in excl):
                    continue
                a = prev.get(p, ZERO)
                b = cur.get(p, ZERO)
                if a == b:
                    continue
                hot += 1
                xa = int.from_bytes(a, "big") ^ int.from_bytes(b, "big")
                if not xa:
                    continue
                xb = xa.to_bytes(PAGE, "big")
                oc = once.get(p)
                wh = when.get(p)
                bf = before.get(p)
                af = after.get(p)
                if oc is None:
                    oc = bytearray(PAGE); once[p] = oc
                    wh = bytearray(PAGE); when[p] = wh
                    bf = bytearray(PAGE); before[p] = bf
                    af = bytearray(PAGE); after[p] = af
                for m in NONZERO.finditer(xb):
                    j = m.start()
                    changed_bytes += 1
                    if oc[j] < 3:
                        oc[j] += 1
                        if oc[j] == 1:
                            wh[j] = i
                            bf[j] = a[j]
                            af[j] = b[j]
            print("     gap %d->%d: hot pages=%d  changed bytes=%d" % (i, i + 1, hot, changed_bytes))
        prev = cur
        del mm
        fh.close()

    # collect "exactly one transition"
    hist = {}
    hits = []            # (addr, before, after, gap_index)
    for p, oc in once.items():
        wh = when[p]; bf = before[p]; af = after[p]
        for j in range(PAGE):
            c = oc[j]
            if c > 3:
                c = 3
            hist[c] = hist.get(c, 0) + 1
            if oc[j] == 1:
                hits.append((p + j, bf[j], af[j], wh[j]))
    print()
    print("transitions histogram (3 means 3+): %s" % sorted(hist.items()))
    print("bytes that changed exactly once: %d" % len(hits))

    if hits:
        addrs = sorted(h[0] for h in hits)
        runs = clusters(addrs)
        print("clusters (gap<=64): %d" % len(runs))
        for start, end, count in runs[:top]:
            span = end - start + 1
            print("  0x%08X..0x%08X  span %-9d n=%-8d density %.2f"
                  % (start, end, span, count, count / float(span)))
        # gap histogram *inside* the biggest cluster -- a per-cell array shows a
        # dominant stride here, noise does not.
        start, end, _c = runs[0]
        inside = [a for a in addrs if start <= a <= end]
        gaps = {}
        for k in range(1, len(inside)):
            g = inside[k] - inside[k - 1]
            if g <= 4096:
                gaps[g] = gaps.get(g, 0) + 1
        print("gap histogram inside the biggest cluster: %s"
              % sorted(gaps.items(), key=lambda kv: -kv[1])[:12])
        ups = sum(1 for _a, o, n, _g in hits if o == 0 and n != 0)
        print("  0 -> nonzero : %d   (others: %d)" % (ups, len(hits) - ups))
        gaps_when = {}
        for _a, _o, _n, g in hits:
            gaps_when[g] = gaps_when.get(g, 0) + 1
        print("  revealed in which gap: %s" % sorted(gaps_when.items()))

    with open(out_path, "w", encoding="utf-8") as fh:
        fh.write("# addr before after gap   (bytes that changed exactly once)\n")
        for addr, o, n, g in sorted(hits):
            fh.write("%08X %02X %02X %d\n" % (addr, o, n, g))
    print("written -> %s" % out_path)


if __name__ == "__main__":
    main()
