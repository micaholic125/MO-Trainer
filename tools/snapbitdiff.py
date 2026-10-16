# Bit-level diff of two MemSnap snapshots.
#
# Motivation: every previous hunt (13 rounds + census) looked for BYTE-level
# structure: runs of >=8/12 changing bytes, uniform strides, one-value tables.
# If the per-house visibility map is BIT-PACKED (1 bit per cell, maybe one
# plane per house), a satellite revealing 349 cells changes ~349 BITS but each
# byte only 0..8 bits and often non-adjacent bits -- no byte-level run
# anywhere.  That is consistent with ALL negative results so far.
#
# This tool diffs a.snap vs b.snap at bit level and reports:
#   * total changed bits per 64KB region, ranked;
#   * for the top regions, the changed-bit ADDRESSES (absolute) as a sample,
#     and whether changed bits cluster (bits/byte histogram);
#   * optionally, check: does byte address X's bit position map to
#     idx = (Y<<9)+X style structure -- we test "bit index within byte +
#     byte offset maps to the known revealed disk" by comparing with the
#     cell-level truth from the same pair (already known: disk X73..93/Y65..85).
#
# Usage: python snapbitdiff.py <a.snap> <b.snap> [--region 0x1E5E0000-0x1EA10000]
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
            raise SystemExit("bad magic")
        pos = 8
        bs = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
        if bs != 0x10000:
            raise SystemExit("bad block size")
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
            raise SystemExit("bad kind")


def readable_span(snap, addr, size):
    """Return number of bytes actually readable from addr (0 if none)."""
    i = bisect.bisect_right(snap.addrs, addr) - 1
    have = 0
    while size > 0 and i < len(snap.addrs):
        base = snap.addrs[i]
        data = snap.blocks[i]
        if addr + have < base:
            break
        off = addr + have - base
        if off >= len(data):
            i += 1
            continue
        take = min(size - have, len(data) - off)
        have += take
        if have == size:
            break
        i += 1
    return have


def main():
    pa, pb = sys.argv[1], sys.argv[2]
    A = Snap(pa)
    B = Snap(pb)
    print("A:", A.stamp, len(A.blocks), "blocks")
    print("B:", B.stamp, len(B.blocks), "blocks")

    # regions with same base in both snapshots
    basesA = set(A.addrs)
    basesB = set(B.addrs)
    common = sorted(basesA & basesB)
    print("common 64KB blocks:", len(common))

    rows = []
    for base in common:
        ia = A.addrs.index(base)
        ib = B.addrs.index(base)
        da = A.blocks[ia]
        db = B.blocks[ib]
        if len(da) != len(db):
            continue
        nbits = 0
        nbytes = 0
        bits_per_byte = {}
        for i in range(len(da)):
            x = da[i] ^ db[i]
            if x:
                cnt = bin(x).count("1")
                nbits += cnt
                nbytes += 1
                bits_per_byte[cnt] = bits_per_byte.get(cnt, 0) + 1
        if nbits:
            rows.append((base, nbits, nbytes, bits_per_byte))

    rows.sort(key=lambda r: -r[1])
    print()
    print("top 20 regions by changed bits:")
    for base, nbits, nbytes, bpb in rows[:20]:
        spread = "1-2bits: %d bytes, 3-4: %d, 5+: %d" % (
            sum(v for k, v in bpb.items() if k <= 2),
            sum(v for k, v in bpb.items() if 3 <= k <= 4),
            sum(v for k, v in bpb.items() if k >= 5))
        print("  0x%08X  bits=%-6d bytes=%-6d  %s" % (base, nbits, nbytes, spread))

    # focus: pure-bit-packed candidates = many changed bytes but few bits each
    print()
    print("bit-packed candidates (bytes changed >=300 but avg bits/byte <= 2.5):")
    found = False
    for base, nbits, nbytes, bpb in rows:
        if nbytes >= 300 and nbits / float(nbytes) <= 2.5:
            found = True
            print("  0x%08X  bits=%d bytes=%d avg=%.2f" % (base, nbits, nbytes, nbits / float(nbytes)))
    if not found:
        print("  (none)")


if __name__ == "__main__":
    main()
