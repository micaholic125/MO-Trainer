# Read the per-cell Shroudedness field out of two MemSnap snapshots
# (before/after the user pressed the spy satellite) and diff them.
#
#   python snapshroud.py <a.snap> <b.snap>
#
# Chain (verified offline against the Ares crash dump, 10527/10527 cells):
#   0x87F7E8  MapClass instance (global object in .data, RW, always captured)
#     +0x13C  Cells.Items  -> CellClass* array
#     +0x140  Cells.Capacity
#   CellClass +0x24 X(short) +0x26 Y(short) +0x120 Shroudedness +0x121 Foggedness
#   0xFE = shrouded, 0xFF = visible, 0..0x30 = fade-in/out anim frame
#
# Read-only: opens two files, prints a diff. Nothing is written anywhere.

import bisect
import sys

MAGIC = b"MOSNAP01"
MAP = 0x87F7E8


class Snap(object):
    def __init__(self, path):
        self.addrs = []
        self.blocks = []
        with open(path, "rb") as fh:
            raw = fh.read()
        if raw[:8] != MAGIC:
            raise SystemExit("%s: bad magic" % path)
        pos = 8
        bs = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
        if bs != 0x10000:
            raise SystemExit("unexpected block size 0x%X" % bs)
        sl = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
        self.stamp = raw[pos:pos + sl].decode("utf-8", "replace"); pos += sl
        while pos < len(raw):
            kind = raw[pos]; pos += 1
            if kind == 0:
                break
            if kind == 1:
                pos += 24
                continue
            if kind == 2:
                addr = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
                ln = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
                self.addrs.append(addr)
                self.blocks.append(raw[pos:pos + ln]); pos += ln
                continue
            raise SystemExit("bad record kind %d at %d" % (kind, pos))

    def read(self, addr, size):
        i = bisect.bisect_right(self.addrs, addr) - 1
        out = b""
        while size > 0 and i >= 0 and i < len(self.addrs):
            base = self.addrs[i]
            data = self.blocks[i]
            off = addr - base
            if off < 0:
                break
            if off >= len(data):
                i += 1  # gap between blocks: try next base? no - bases are sorted ascending
                break
            take = min(size, len(data) - off)
            out += data[off:off + take]
            addr += take
            size -= take
            i += 1
        return out if len(out) == size or size == 0 else (out + b"\x00" * (size - len(out)))

    def u32(self, a):
        b = self.read(a, 4)
        return int.from_bytes(b, "little") if len(b) == 4 else None

    def u16(self, a):
        b = self.read(a, 2)
        return int.from_bytes(b, "little") if len(b) == 2 else None


def collect(snap):
    items = snap.u32(MAP + 0x13C)
    cap = snap.u32(MAP + 0x140)
    if not items or not cap:
        raise SystemExit("chain broken in snapshot %s" % snap.stamp)
    grid = {}
    n = 0
    for idx in range(cap):
        p = snap.u32(items + 4 * idx)
        if not p:
            continue
        b = snap.read(p + 0x24, 4)
        if len(b) < 4:
            continue
        X = int.from_bytes(b[0:2], "little")
        Y = int.from_bytes(b[2:4], "little")
        s = snap.read(p + 0x120, 2)
        if len(s) < 2:
            continue
        grid[(X, Y)] = (s[0], s[1])
        n += 1
    return grid, n


def render(gridA, gridB, maxX, maxY, mode):
    chars = []
    for Y in range(maxY + 1):
        row = []
        for X in range(maxX + 1):
            a = gridA.get((X, Y))
            b = gridB.get((X, Y))
            if a is None and b is None:
                row.append(" ")
            elif mode == "before":
                v = a[0] if a else None
                row.append(" " if v is None else
                           ("#" if v == 0xFE else "." if v == 0xFF else "+"))
            elif mode == "after":
                v = b[0] if b else None
                row.append(" " if v is None else
                           ("#" if v == 0xFE else "." if v == 0xFF else "+"))
            else:  # diff
                if a is None or b is None:
                    row.append(" ")
                    continue
                if a[0] == 0xFE and b[0] == 0xFF:
                    row.append("R")  # revealed!
                elif a[0] == 0xFF and b[0] == 0xFE:
                    row.append("L")  # lost
                elif a[0] != b[0]:
                    row.append("~")  # anim frame change
                else:
                    row.append("." if b[0] == 0xFF else
                                "#" if b[0] == 0xFE else "+")
        chars.append("".join(row))
    return chars


def main(pa, pb):
    A = Snap(pa)
    B = Snap(pb)
    print("snap A: %s  (%d blocks)" % (A.stamp, len(A.blocks)))
    print("snap B: %s  (%d blocks)" % (B.stamp, len(B.blocks)))
    gA, nA = collect(A)
    gB, nB = collect(B)
    print("cells A = %d, cells B = %d" % (nA, nB))

    hist = {}
    for v in (gA[k][0] for k in gA):
        hist[v] = hist.get(v, 0) + 1
    print("A Shroudedness: FE(shrouded)=%d FF(visible)=%d anim=%d other=%d"
          % (hist.get(0xFE, 0), hist.get(0xFF, 0),
             sum(c for v, c in hist.items() if v <= 0x30),
             sum(c for v, c in hist.items() if 0x30 < v < 0xFE)))
    hist = {}
    for v in (gB[k][0] for k in gB):
        hist[v] = hist.get(v, 0) + 1
    print("B Shroudedness: FE(shrouded)=%d FF(visible)=%d anim=%d other=%d"
          % (hist.get(0xFE, 0), hist.get(0xFF, 0),
             sum(c for v, c in hist.items() if v <= 0x30),
             sum(c for v, c in hist.items() if 0x30 < v < 0xFE)))

    keys = set(gA) | set(gB)
    both = [k for k in keys if k in gA and k in gB]
    rev = [k for k in both if gA[k][0] == 0xFE and gB[k][0] == 0xFF]
    lost = [k for k in both if gA[k][0] == 0xFF and gB[k][0] == 0xFE]
    anim = [k for k in both if gA[k][0] != gB[k][0]
            and k not in rev and k not in lost]
    fog_rev = [k for k in both if gA[k][1] == 0xFE and gB[k][1] == 0xFF]
    print()
    print("DIFF  shrouded->visible : %d   <-- satellite reveal?" % len(rev))
    print("DIFF  visible->shrouded : %d" % len(lost))
    print("DIFF  anim-frame changes: %d" % len(anim))
    print("DIFF  fogged->visible   : %d" % len(fog_rev))
    if rev:
        xs = [k[0] for k in rev]
        ys = [k[1] for k in rev]
        print("reveal bbox: X %d..%d  Y %d..%d" %
              (min(xs), max(xs), min(ys), max(ys)))

    maxX = max(k[0] for k in keys)
    maxY = max(k[1] for k in keys)
    for name, mode in (("BEFORE (A)", "before"), ("AFTER  (B)", "after"),
                       ("DIFF  (R=revealed)", "diff")):
        print()
        print("%s   #=shrouded .=visible +=anim R=FE->FF L=FF->FE ~=anim-change" % name)
        for line in render(gA, gB, maxX, maxY, mode):
            print(line)


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("usage: python snapshroud.py <a.snap> <b.snap>")
    main(sys.argv[1], sys.argv[2])
