# Offline chain verifier: check the MapClass -> Cells -> CellClass layout
# against an Ares crash minidump (read-only, no game process involved).
#
#   python mdmp_verify.py <path-to-extcrashdump.dmp>
#
# All offsets come from the Ares team's YRpp reverse-engineered headers
# (YR 1.001 - the exact unmodified binary this project targets):
#
#   MapClass / GScreenClass instance : 0x87F7E8  (global object, not a pointer)
#     +0xEC  MapRect      (x, y, w, h as 4 ints)
#     +0x138 Cells, a VectorClass<CellClass*>:
#              +0x0 vtable, +0x4 Items (ptr array), +0x8 Capacity
#     +0x148 MaxWidth, +0x14C MaxHeight, +0x150 MaxNumCells
#   CellClass (size 0x148):
#     +0x00 vtable, +0x24 X (short), +0x26 Y (short),
#     +0x120 Shroudedness char  (-2 = shrouded, -1 = visible, 0..48 = anim frame)
#     +0x121 Foggedness   char  (same value set)
#   Index formula (MapClass::GetCellIndex): idx = (Y << 9) + X
#
# Decisive check: for every non-null cell, the cell's own stored X/Y must
# reproduce its slot index in the pointer array.  Random data cannot do that.

import bisect
import struct
import sys

MODULE_LO = 0x400000
MODULE_HI = 0x400000 + 0x793000  # gamemd.exe module span (no ASLR)


class Dump(object):
    def __init__(self, path):
        self.f = open(path, "rb")
        hdr = self.f.read(32)
        if hdr[:4] != b"MDMP":
            raise SystemExit("not an MDMP file: %s" % path)
        n_streams, dir_rva = struct.unpack_from("<II", hdr, 8)
        self.f.seek(dir_rva)
        raw = self.f.read(12 * n_streams)
        self.streams = {}
        for i in range(n_streams):
            st, dsz, rva = struct.unpack_from("<III", raw, 12 * i)
            self.streams.setdefault(st, (dsz, rva))
        self.ranges = []  # (start_addr, file_off, size)
        if 9 in self.streams:  # Memory64ListStream
            _, rva = self.streams[9]
            self.f.seek(rva)
            n, base_rva = struct.unpack("<QQ", self.f.read(16))
            desc = self.f.read(16 * n)
            off = base_rva
            for i in range(n):
                start, dsz2 = struct.unpack_from("<QQ", desc, 16 * i)
                if dsz2:
                    self.ranges.append((start, off, dsz2))
                off += dsz2
        elif 5 in self.streams:  # MemoryListStream
            _, rva = self.streams[5]
            self.f.seek(rva)
            (n,) = struct.unpack("<I", self.f.read(4))
            for i in range(n):
                start, dsz2, rva2 = struct.unpack("<QII", self.f.read(16))
                if dsz2:
                    self.ranges.append((start, rva2, dsz2))
        else:
            raise SystemExit("no memory streams in dump")
        self.ranges.sort()
        merged = []
        for start, off, sz in self.ranges:
            if merged and merged[-1][0] + merged[-1][2] == start \
                    and merged[-1][1] + merged[-1][2] == off:
                merged[-1][2] += sz
            else:
                merged.append([start, off, sz])
        self.ranges = [tuple(x) for x in merged]
        self.starts = [r[0] for r in self.ranges]
        print("dump: %d memory ranges (after merge), %d bytes"
              % (len(self.ranges), sum(r[2] for r in self.ranges)))

    def read(self, addr, size):
        i = bisect.bisect_right(self.starts, addr) - 1
        if i < 0:
            return None
        start, off, sz = self.ranges[i]
        if start <= addr and addr + size <= start + sz:
            self.f.seek(off + addr - start)
            return self.f.read(size)
        return None

    def u32(self, a):
        b = self.read(a, 4)
        return None if b is None or len(b) < 4 else struct.unpack("<I", b)[0]

    def u16(self, a):
        b = self.read(a, 2)
        return None if b is None or len(b) < 2 else struct.unpack("<H", b)[0]

    def u8(self, a):
        b = self.read(a, 1)
        return None if b is None or len(b) < 1 else b[0]


def main(path):
    d = Dump(path)

    # --- the old claim: "MapClass at 0x87F8DC" (informational) ---
    old = d.u32(0x87F8DC)
    print("[info] 0x87F8DC first dword = %s"
          % ("0x%08X" % old if old is not None else "unreadable"))

    # --- money chain cross-check (project-calibrated statics) ---
    house = d.u32(0x00A83D4C)
    money = d.u32(house + 0x30C) if house else None
    print("[info] house @ 0x%08X, house+0x30C = %s"
          % (house if house else 0, money if money is not None else "?"))

    # --- MapClass instance ---
    MAP = 0x87F7E8
    vptr = d.u32(MAP)
    ok_v = vptr is not None and MODULE_LO <= vptr < MODULE_HI
    print("MapClass @ 0x%08X vtable = %s (%s)"
          % (MAP, "0x%08X" % vptr if vptr else "?",
             "in module" if ok_v else "OUT OF MODULE"))
    mr = [d.u32(MAP + 0xEC + i * 4) for i in range(4)]
    print("MapRect (x,y,w,h) = %s" % mr)

    items = d.u32(MAP + 0x13C)
    cap = d.u32(MAP + 0x140)
    init = d.u8(MAP + 0x144)
    alloc = d.u8(MAP + 0x145)
    print("Cells: items = %s capacity = %s IsInitialized = %s IsAllocated = %s"
          % ("0x%08X" % items if items else "?", cap, init, alloc))
    mw = d.u32(MAP + 0x148)
    mh = d.u32(MAP + 0x14C)
    mnc = d.u32(MAP + 0x150)
    print("Map dims: MaxWidth = %s MaxHeight = %s MaxNumCells = %s" % (mw, mh, mnc))
    if not items or not cap:
        raise SystemExit("chain broken at Cells vector")

    # --- walk every cell ---
    n_nonnull = n_null = n_unread = 0
    n_vtable = n_idx = n_inmap = 0
    sh_hist = {}
    fg_hist = {}
    agree = 0
    grid = {}   # (X, Y) -> (sh, fg)
    for idx in range(cap):
        p = d.u32(items + 4 * idx)
        if p is None:
            n_unread += 1
            continue
        if p == 0:
            n_null += 1
            continue
        n_nonnull += 1
        cp = d.u32(p)
        if cp is not None and MODULE_LO <= cp < MODULE_HI:
            n_vtable += 1
        X = d.u16(p + 0x24)
        Y = d.u16(p + 0x26)
        sh = d.u8(p + 0x120)
        fg = d.u8(p + 0x121)
        if X is None or Y is None or sh is None:
            n_unread += 1
            continue
        if idx == ((Y << 9) + X):
            n_idx += 1
        if X < 512 and (not mr or (mr[0] <= X < mr[0] + mr[2]
                                   and mr[1] <= Y < mr[1] + mr[3])):
            n_inmap += 1
            grid[(X, Y)] = (sh, fg)
        sh_hist[sh] = sh_hist.get(sh, 0) + 1
        fg_hist[fg] = fg_hist.get(fg, 0) + 1
        if sh == fg:
            agree += 1

    print()
    print("cells: non-null = %d  null = %d  unread = %d" % (n_nonnull, n_null, n_unread))
    if n_nonnull:
        print("vtable in module      : %d / %d  (%.1f%%)"
              % (n_vtable, n_nonnull, 100.0 * n_vtable / n_nonnull))
        print("idx == (Y<<9)+X       : %d / %d  (%.1f%%)   <-- decisive"
              % (n_idx, n_nonnull, 100.0 * n_idx / n_nonnull))
        print("coords inside map     : %d / %d" % (n_inmap, n_nonnull))
        print("shroudedness == foggedness: %d / %d" % (agree, n_nonnull))
    print()
    print("Shroudedness histogram (value: count):")
    for v in sorted(sh_hist):
        note = ""
        if v == 0xFE:
            note = "  (-2 = shrouded)"
        elif v == 0xFF:
            note = "  (-1 = visible)"
        elif v <= 0x30:
            note = "  (anim frame)"
        print("  0x%02X : %d%s" % (v, sh_hist[v], note))
    print("Foggedness histogram (value: count):")
    for v in sorted(fg_hist):
        note = ""
        if v == 0xFE:
            note = "  (-2 = fogged)"
        elif v == 0xFF:
            note = "  (-1 = visible)"
        elif v <= 0x30:
            note = "  (anim frame)"
        print("  0x%02X : %d%s" % (v, fg_hist[v], note))

    if not grid:
        return
    maxX = max(k[0] for k in grid)
    maxY = max(k[1] for k in grid)
    print()
    print("Shroud map render (# = shrouded, . = visible, + = anim, ' ' = no cell):")
    for Y in range(maxY + 1):
        row = []
        for X in range(maxX + 1):
            c = grid.get((X, Y))
            if c is None:
                row.append(" ")
            elif c[0] == 0xFE:
                row.append("#")
            elif c[0] == 0xFF:
                row.append(".")
            elif c[0] <= 0x30:
                row.append("+")
            else:
                row.append("?")
        print("".join(row))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: python mdmp_verify.py <dump.dmp>")
    main(sys.argv[1])
