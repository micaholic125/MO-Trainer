# One-off dev helper: search a MemSnap snapshot for a byte pattern (or an ASCII
# string), printing the address and a short ASCII context around each hit.
#
#   python snapgrep.py snapfile "Size=" [--max 20] [--ctx 48]
#   python snapgrep.py snapfile --hex 000102 [--max 20]
#
# Read-only.
import sys

MAGIC = b"MOSNAP01"


def records(path):
    raw = open(path, "rb").read()
    if raw[:8] != MAGIC:
        raise SystemExit("bad magic")
    pos = 8
    pos += 4
    stamp_len = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
    stamp = raw[pos:pos + stamp_len].decode("utf-8", "replace"); pos += stamp_len
    while pos < len(raw):
        kind = raw[pos]; pos += 1
        if kind == 0:
            break
        if kind == 1:
            pos += 24
            continue
        addr = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
        ln = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
        yield addr, raw[pos:pos + ln]
        pos += ln


def printable(b):
    return "".join(chr(c) if 32 <= c < 127 else "." for c in b)


def main():
    path = sys.argv[1]
    arg = sys.argv[2]
    if arg == "--hex":
        pat = bytes.fromhex(sys.argv[3])
    else:
        pat = arg.encode("latin-1")
    mx = 20
    if "--max" in sys.argv:
        mx = int(sys.argv[sys.argv.index("--max") + 1])
    ctx = 48
    if "--ctx" in sys.argv:
        ctx = int(sys.argv[sys.argv.index("--ctx") + 1])

    hits = 0
    for addr, data in records(path):
        start = 0
        while True:
            i = data.find(pat, start)
            if i < 0:
                break
            lo = max(0, i - ctx // 2)
            hi = min(len(data), i + len(pat) + ctx)
            print("0x%08X  ...%s..." % (addr + i, printable(data[lo:hi])))
            hits += 1
            if hits >= mx:
                print("(stopping at %d hits)" % mx)
                return
            start = i + 1
    print("total hits: %d" % hits)


if __name__ == "__main__":
    main()
