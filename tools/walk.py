# One-off dev helper: dump an object out of a MemSnap snapshot and follow any
# pointers it contains one level deep -- the standard move when you have found
# "an object" (e.g. MapClass) and want to reach the arrays hanging off it.
#
#   python walk.py snapfile 0x0087F8D0 [--len 512] [--follow]
#   python walk.py snapfile --ptr 0x00683D4C [--len 512] [--follow]
#
# --ptr means "the address holds a pointer; dump what it points at".
# Read-only.
import sys

MAGIC = b"MOSNAP01"
MODULE_LO, MODULE_HI = 0x400000, 0xB93000


def records(path):
    raw = open(path, "rb").read()
    if raw[:8] != MAGIC:
        raise SystemExit("bad magic")
    pos = 8
    pos += 4
    sl = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4 + sl
    while pos < len(raw):
        k = raw[pos]; pos += 1
        if k == 0:
            break
        if k == 1:
            pos += 24
            continue
        a = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
        ln = int.from_bytes(raw[pos:pos + 4], "little"); pos += 4
        yield a, raw[pos:pos + ln]
        pos += ln


def main():
    snap = sys.argv[1]
    arg = sys.argv[2]
    ptr_mode = arg == "--ptr"
    if ptr_mode:
        root = int(sys.argv[3], 16)
    else:
        root = int(arg, 16)
    length = 512
    if "--len" in sys.argv:
        length = int(sys.argv[sys.argv.index("--len") + 1], 0)
    follow = "--follow" in sys.argv

    hi = 0
    for a, d in records(snap):
        hi = max(hi, a + len(d))
    hi += 0x10000
    buf = bytearray(hi)
    for a, d in records(snap):
        buf[a:a + len(d)] = d

    if ptr_mode:
        target = int.from_bytes(buf[root:root + 4], "little")
        print("[0x%08X] = 0x%08X" % (root, target))
        base = target
    else:
        base = root

    print("=== dump 0x%08X .. 0x%08X ===" % (base, base + length))
    for a in range(base, base + length, 4):
        v = int.from_bytes(buf[a:a + 4], "little")
        s = int.from_bytes(buf[a:a + 4], "little", signed=True)
        f = 0.0
        try:
            import struct
            f = struct.unpack("<f", bytes(buf[a:a + 4]))[0]
        except Exception:
            pass
        tag = ""
        if 0x10000 <= v < 0x7F000000:
            t = int.from_bytes(buf[v:v + 4], "little")
            tag = "-> 0x%08X [%08X]" % (v, t)
            if MODULE_LO <= t < MODULE_HI:
                tag += "  vtable-in-module"
        elif v != 0:
            tag = "(int %d / %.3f)" % (s, f)
        print("  0x%08X  %08X  %s" % (a, v, tag))

    if follow:
        print()
        print("=== 跟着每个像指针的值进去看一眼 ===")
        seen = set()
        for a in range(base, base + length, 4):
            v = int.from_bytes(buf[a:a + 4], "little")
            if not (0x10000 <= v < 0x7F000000) or v in seen:
                continue
            seen.add(v)
            head = bytes(buf[v:v + 32])
            hexs = " ".join("%02X" % b for b in head[:16])
            t = int.from_bytes(head[:4], "little")
            kind = ""
            if MODULE_LO <= t < MODULE_HI:
                kind = "  vtable=0x%08X" % t
            print("  +0x%03X -> 0x%08X  %s%s" % (a - base, v, hexs, kind))


if __name__ == "__main__":
    main()
