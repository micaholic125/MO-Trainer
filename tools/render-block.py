# 把 diff2.txt 里某个地址那一段画成字符图（看清形状）。
import io, re, sys

path = sys.argv[1]
want = int(sys.argv[2], 16)
cols = int(sys.argv[3]) if len(sys.argv) > 3 else 128

text = io.open(path, encoding="utf-8", errors="replace").read()

cur_addr = None
cur = []
target = None
blocks = {}
for line in text.split("\n"):
    m = re.match(r"^--- 变了：0x([0-9A-F]{8})", line)
    if m:
        if cur_addr is not None:
            blocks[cur_addr] = bytes(cur)
        cur_addr = int(m.group(1), 16)
        cur = []
        continue
    m = re.match(r"^  ([0-9A-F]{4})  ((?:[0-9A-F]{2} ){16})", line)
    if m and cur_addr is not None:
        cur.extend(int(m.group(2)[i:i+2], 16) for i in range(0, 48, 3))
if cur_addr is not None:
    blocks[cur_addr] = bytes(cur)

data = blocks.get(want)
if data is None:
    print("没找到 0x%08X，可选：" % want)
    print(" ".join("0x%08X" % a for a in sorted(blocks)))
    sys.exit(1)

# 统计
hist = {}
for b in data:
    hist[b] = hist.get(b, 0) + 1
top = sorted(hist.items(), key=lambda kv: -kv[1])[:6]
print("0x%08X 长度 %d" % (want, len(data)))
print("值分布：" + "  ".join("%02X:%d" % (v, c) for v, c in top))
print()

# 最多的值画 '.'、次多 '#', 第三 'o', 其余 '+'
a = top[0][0]
b = top[1][0] if len(top) > 1 else -1
c = top[2][0] if len(top) > 2 else -1
rows = (len(data) + cols - 1) // cols
for r in range(min(rows, 64)):
    line = []
    for i in range(cols):
        idx = r * cols + i
        if idx >= len(data):
            break
        v = data[idx]
        if v == a:
            line.append(".")
        elif v == b:
            line.append("#")
        elif v == c:
            line.append("o")
        else:
            line.append("+")
    print("".join(line))
