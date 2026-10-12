# One-off dev helper: eyeball a PGM (P5) as ASCII, and report where the dark
# ("never explored") pixels are.  Usage: pgm-inspect.py file.pgm [cols]
import sys

path = sys.argv[1]
cols = int(sys.argv[2]) if len(sys.argv) > 2 else 80

with open(path, "rb") as fh:
    raw = fh.read()

# parse P5 header
pos = 0
fields = []
while len(fields) < 4:
    # skip whitespace and comments
    while raw[pos:pos+1].isspace():
        pos += 1
    if raw[pos:pos+1] == b"#":
        while raw[pos:pos+1] not in (b"\n", b""):
            pos += 1
        continue
    start = pos
    while not raw[pos:pos+1].isspace():
        pos += 1
    fields.append(raw[start:pos])
pos += 1
magic, w, h, maxv = fields[0], int(fields[1]), int(fields[2]), int(fields[3])
px = raw[pos:pos + w * h]
print("PGM %s %dx%d maxv=%d  pixels=%d" % (magic.decode(), w, h, maxv, len(px)))

# downsample to cols x rows for ASCII
rows = max(1, int(round(h * cols / float(w) / 2.0)))   # char cells are ~2:1
print("ascii %d x %d" % (cols, rows))
ramp = " .:-=+*#%@"
for r in range(rows):
    y0 = r * h // rows
    y1 = max(y0 + 1, (r + 1) * h // rows)
    line = []
    for c in range(cols):
        x0 = c * w // cols
        x1 = max(x0 + 1, (c + 1) * w // cols)
        s = 0
        n = 0
        for y in range(y0, y1):
            base = y * w
            for x in range(x0, x1):
                s += px[base + x]
                n += 1
        v = s // max(1, n)
        line.append(ramp[min(9, v * 10 // 256)])
    print("%4d |%s|" % (y0, "".join(line)))

# column profile of very dark pixels
print()
print("Dark(<24) pixel share per 10%% band of width:")
for b in range(10):
    x0 = b * w // 10
    x1 = (b + 1) * w // 10
    n = 0
    tot = 0
    for y in range(h):
        base = y * w
        for x in range(x0, x1):
            tot += 1
            if px[base + x] < 24:
                n += 1
    print("  cols %4d-%4d : %5.1f%%" % (x0, x1, 100.0 * n / max(1, tot)))
