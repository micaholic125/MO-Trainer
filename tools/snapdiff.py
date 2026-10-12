# One-off dev helper: compare two MemSnap snapshots byte by byte.
#
#   python snapdiff.py a.snap b.snap                 -> summary only
#   python snapdiff.py a.snap b.snap --dump out.txt  -> also write every changed byte
#   python snapdiff.py --cross a.txt b.txt           -> intersect two dumped change lists
#
# Snapshot format is described in tools/MemSnap.cs.  Reading is read-only.
import sys

MAGIC = b"MOSNAP01"


def load(path):
    """Return {block_addr: bytearray} plus the header stamp."""
    blocks = {}
    stamp = "?"
    with open(path, "rb") as fh:
        raw = fh.read()
    if raw[:8] != MAGIC:
        raise SystemExit("%s: bad magic" % path)
    pos = 8
    block_size = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
    stamp_len = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
    stamp = raw[pos:pos + stamp_len].decode("utf-8", "replace"); pos += stamp_len
    while pos < len(raw):
        kind = raw[pos]; pos += 1
        if kind == 0:
            break
        if kind == 1:
            pos += 8 + 8 + 4 + 4
            continue
        if kind == 2:
            addr = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
            ln = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
            blocks[addr] = bytearray(raw[pos:pos + ln]); pos += ln
            continue
        raise SystemExit("bad record kind %d at %d" % (kind, pos))
    return block_size, stamp, blocks


def diff(blocks_a, blocks_b):
    """Return {addr: (old, new)} for every byte that differs; a missing block is zeros.

    NOTE: block addresses are *not* 64 KB aligned (they sit at each region's own base),
    so blocks from two snapshots can overlap.  Keying by absolute address is what keeps
    a byte from being counted twice -- an earlier list-based version did exactly that.
    """
    changes = {}
    zero = bytearray()
    for base in sorted(set(blocks_a) | set(blocks_b)):
        a = blocks_a.get(base, zero)
        b = blocks_b.get(base, zero)
        # fast path: identical block of the same length (C-level compare)
        if len(a) == len(b) and a == b:
            continue
        n = max(len(a), len(b))
        for i in range(n):
            va = a[i] if i < len(a) else 0
            vb = b[i] if i < len(b) else 0
            if va != vb:
                changes[base + i] = (va, vb)
    return changes


def clusters(addrs, gap=64):
    """Group sorted addresses into runs, returning (start, end, count)."""
    runs = []
    start = prev = None
    count = 0
    for addr in addrs:
        if start is None:
            start = prev = addr
            count = 1
        elif addr - prev <= gap:
            prev = addr
            count += 1
        else:
            runs.append((start, prev, count))
            start = prev = addr
            count = 1
    if start is not None:
        runs.append((start, prev, count))
    runs.sort(key=lambda r: -(r[2]))
    return runs


def summarise(changed):
    print("changed bytes: %d" % len(changed))
    if not changed:
        return
    addrs = sorted(changed)
    runs = clusters(addrs)
    print("clusters (gap<=64): %d" % len(runs))
    for start, end, count in runs[:20]:
        span = end - start + 1
        print("  0x%08X..0x%08X  span %-8d changed %-8d density %.2f"
              % (start, end, span, count, count / float(span)))
    up = sum(1 for o, n in changed.values() if o == 0 and n != 0)
    down = sum(1 for o, n in changed.values() if o != 0 and n == 0)
    other = len(changed) - up - down
    print("  0->nonzero %d | nonzero->0 %d | other %d" % (up, down, other))


def cross(pa, pb):
    """Intersect two dumped change lists by address."""
    def load_list(p):
        d = {}
        with open(p, "r", encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                parts = line.split()
                d[int(parts[0], 16)] = (int(parts[1], 16), int(parts[2], 16))
        return d
    a = load_list(pa)
    b = load_list(pb)
    sa, sb = set(a), set(b)
    common = sorted(sa & sb)
    only = sorted(sa ^ sb)
    print("A=%d B=%d  变过两次(噪音嫌疑)=%d  只变过一次=%d"
          % (len(a), len(b), len(common), len(only)))
    # 「只变过一次」才是要找的东西：计时器/动画这类噪音两个区间里都会变，
    # 而"某一格黑幕被揭开"一辈子只变一次 —— 它只会出现在两个区间中的一个里。
    runs = []
    start = prev = None
    count = 0
    for addr in only:
        if start is None:
            start = prev = addr
            count = 1
        elif addr - prev <= 64:
            prev = addr
            count += 1
        else:
            runs.append((start, prev, count))
            start = prev = addr
            count = 1
    if start is not None:
        runs.append((start, prev, count))
    runs.sort(key=lambda r: -r[2])
    print("其中按邻近分簇：%d 簇；最大的几簇：" % len(runs))
    for start, end, count in runs[:25]:
        span = end - start + 1
        print("  0x%08X..0x%08X  span %-8d n=%-8d density %.2f"
              % (start, end, span, count, count / float(span)))
    only_path = pa + ".only.txt"
    with open(only_path, "w", encoding="utf-8") as fh:
        fh.write("# addr old new  (只在一个区间里变过)\n")
        for addr in only:
            o, n = a.get(addr) or b.get(addr)
            fh.write("%08X %02X %02X\n" % (addr, o, n))
    print("只变过一次的明细已写入 %s" % only_path)


def main():
    if len(sys.argv) >= 4 and sys.argv[1] == "--cross":
        cross(sys.argv[2], sys.argv[3])
        return
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    bs, sa, A = load(sys.argv[1])
    bs2, sb, B = load(sys.argv[2])
    print("A: %s  blocks=%d" % (sa, len(A)))
    print("B: %s  blocks=%d" % (sb, len(B)))
    changed = diff(A, B)
    summarise(changed)
    if "--dump" in sys.argv:
        i = sys.argv.index("--dump")
        out = sys.argv[i + 1] if len(sys.argv) > i + 1 else "changes.txt"
        with open(out, "w", encoding="utf-8") as fh:
            fh.write("# addr old new  (hex)\n")
            for addr in sorted(changed):
                o, n = changed[addr]
                fh.write("%08X %02X %02X\n" % (addr, o, n))
        print("dumped %d rows -> %s" % (len(changed), out))


if __name__ == "__main__":
    main()
