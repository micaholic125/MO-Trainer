# One-off dev helper: render a range of a MemSnap snapshot as a 2-D character
# grid, so a candidate can be eyeballed against the player's minimap screenshot.
#
#   python render-snap.py snapfile 0xSTART [--stride N] [--width N] [--rows N]
#                                   [--mask once.txt] [--span N]
#
# --mask renders "which addresses changed exactly once" instead of raw values.
# Read-only.
import sys

MAGIC = b"MOSNAP01"


def load_pages(path, lo, hi):
    """Return {page_addr: bytes} limited to the requested window."""
    pages = {}
    raw = open(path, "rb").read()
    if raw[:8] != MAGIC:
        raise SystemExit("bad magic")
    pos = 8
    pos += 4
    stamp_len = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
    pos += stamp_len
    while pos < len(raw):
        kind = raw[pos]; pos += 1
        if kind == 0:
            break
        if kind == 1:
            pos += 24
            continue
        addr = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
        ln = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
        if addr + ln > lo and addr < hi:
            data = raw[pos:pos + ln]
            for i in range(0, ln, PAGE):
                a = addr + i
                if a + PAGE <= lo or a >= hi:
                    continue
                pages[a] = data[i:i + PAGE]
        pos += ln
    return pages


PAGE = 4096
ZERO = bytes(PAGE)


def main():
    path = sys.argv[1]
    start = int(sys.argv[2], 16)
    def opt(name, dflt):
        if name in sys.argv:
            return int(sys.argv[sys.argv.index(name) + 1], 0)
        return dflt
    stride = opt("--stride", 1)
    width = opt("--width", 128)
    rows = opt("--rows", 48)
    span = opt("--span", 0)
    mask_path = None
    if "--mask" in sys.argv:
        mask_path = sys.argv[sys.argv.index("--mask") + 1]

    n = width * rows * stride + 64
    if span <= 0:
        span = n
    lo = start & ~(PAGE - 1)
    hi = start + span + PAGE
    pages = load_pages(path, lo, hi)

    def byte_at(a):
        p = pages.get(a & ~(PAGE - 1))
        if p is None:
            return 0
        return p[a & (PAGE - 1)]

    mask = None
    if mask_path:
        mask = set()
        for line in open(mask_path, encoding="utf-8"):
            if line.startswith("#"):
                continue
            mask.add(int(line.split()[0], 16))

    # gather values
    vals = []
    for i in range(width * rows):
        a = start + i * stride
        if mask is not None:
            vals.append(1 if a in mask else 0)
        else:
            vals.append(byte_at(a))

    hist = {}
    for v in vals:
        hist[v] = hist.get(v, 0) + 1
    top = sorted(hist.items(), key=lambda kv: -kv[1])[:6]
    print("0x%08X stride=%d width=%d rows=%d span=%d %s"
          % (start, stride, width, rows, span, "(MASK: 1 = changed exactly once)" if mask else ""))
    print("value histogram: " + "  ".join("%02X:%d" % (v, c) for v, c in top))
    a0 = top[0][0]
    b0 = top[1][0] if len(top) > 1 else -1
    c0 = top[2][0] if len(top) > 2 else -1
    print()
    for r in range(rows):
        line = []
        for c in range(width):
            v = vals[r * width + c]
            if v == a0:
                line.append(".")
            elif v == b0:
                line.append("#")
            elif v == c0:
                line.append("o")
            else:
                line.append("+")
        print("%4d %s" % (start + r * width * stride, "".join(line)))
    print("(header address = address of the first byte of that row)")


if __name__ == "__main__":
    main()
