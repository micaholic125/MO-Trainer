# 分析 diff2.txt 里倒出来的 16 KB 数据块，按"像地图"的程度排序。
# 像地图 = 只有两三种值占绝对多数 + 成段（平均连续长度中等）+ 少数方有份量。
import io, re, sys

path = sys.argv[1]
text = io.open(path, encoding="utf-8", errors="replace").read()

blocks = []   # (address, bytes)
cur_addr = None
cur = []
for line in text.split("\n"):
    m = re.match(r"^--- 变了：0x([0-9A-F]{8})", line)
    if m:
        if cur_addr is not None:
            blocks.append((cur_addr, bytes(cur)))
        cur_addr = int(m.group(1), 16)
        cur = []
        continue
    m = re.match(r"^  ([0-9A-F]{4})  ((?:[0-9A-F]{2} ){16})", line)
    if m and cur_addr is not None:
        cur.extend(int(m.group(2)[i:i+2], 16) for i in range(0, 48, 3))
if cur_addr is not None:
    blocks.append((cur_addr, bytes(cur)))

print("解析出 %d 块" % len(blocks))
print()

rows = []
for addr, data in blocks:
    n = len(data)
    if n < 4096:
        continue
    hist = {}
    for b in data:
        hist[b] = hist.get(b, 0) + 1
    ranked = sorted(hist.items(), key=lambda kv: -kv[1])
    top = ranked[:3]
    two = sum(c for _, c in top[:2]) / float(n)
    minority = (top[1][1] if len(top) > 1 else 0) / float(n)
    # 游程
    runs = 1
    longest = 1
    cur_run = 1
    for i in range(1, n):
        if data[i] == data[i-1]:
            cur_run += 1
        else:
            runs += 1
            longest = max(longest, cur_run)
            cur_run = 1
    longest = max(longest, cur_run)
    avg = n / float(runs)
    rows.append((addr, n, top, two, minority, avg, longest, runs))

# 先只显示"两种值占多数"的
rows.sort(key=lambda r: -(r[3] * min(r[5], 256)))
for addr, n, top, two, minority, avg, longest, runs in rows[:15]:
    tops = " ".join("%02X:%d" % (v, c) for v, c in top)
    print("0x%08X  两值占比 %.1f%%  少数方 %.1f%%  平均连续 %.1f  最长 %d  段数 %d | %s"
          % (addr, two * 100, minority * 100, avg, longest, runs, tops))
