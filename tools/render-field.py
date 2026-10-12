# 从原始字节里抽出"按固定步长排列的某个字段"，铺成二维图，好与用户的小地图截图对形状。
import io, re, sys

path = sys.argv[1]
base = int(sys.argv[2], 16)
stride = int(sys.argv[3])
offset = int(sys.argv[4], 16)
width = int(sys.argv[5]) if len(sys.argv) > 5 else 128

# 解析 hexdump：形如 "  0000  FF FF ... |....|"
data = {}
cur_base = None
for line in io.open(path, encoding="utf-8", errors="replace"):
    m = re.match(r"^--- 0x([0-9A-F]{8}) ", line)
    if m:
        cur_base = int(m.group(1), 16)
        continue
    m = re.match(r"^  ([0-9A-F]{4,6})  ((?:[0-9A-F]{2} )+)", line)
    if m and cur_base is not None:
        off = int(m.group(1), 16)
        raw = m.group(2)
        bs = [int(raw[i:i+2], 16) for i in range(0, len(raw) - 1, 3)]
        for i, b in enumerate(bs):
            data[cur_base + off + i] = b

print("读到 %d 字节（0x%08X 起）" % (len(data), base))

# 抽字段
seq = []
addr = base + offset
while addr in data:
    seq.append(data[addr])
    addr += stride
print("抽出 %d 个值（步长 %d，偏移 0x%X）" % (len(seq), stride, offset))

hist = {}
for v in seq:
    hist[v] = hist.get(v, 0) + 1
top = sorted(hist.items(), key=lambda kv: -kv[1])[:6]
print("取值分布：" + "  ".join("%02X:%d" % (v, c) for v, c in top))
print()

# 铺图：最多的值画 '.'，次多 '#', 第三 'o'，其余 '+'
a = top[0][0]
b = top[1][0] if len(top) > 1 else -1
rows = (len(seq) + width - 1) // width
for r in range(min(rows, 90)):
    line = []
    for c in range(width):
        i = r * width + c
        if i >= len(seq):
            break
        v = seq[i]
        if v == a:
            line.append(".")
        elif v == b:
            line.append("#")
        else:
            line.append("+")
    print("".join(line))
