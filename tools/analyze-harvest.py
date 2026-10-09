# -*- coding: utf-8 -*-
"""离线分析采集报告：把「电力产出」的每一次跳变，与同一采样点上
被跟踪对象的字段变化做交叉比对，找"每座电厂各自产出多少"的那个字段。

只读文件、不碰游戏、不写任何东西。
"""
import io
import re
import sys

path = sys.argv[1]
text = io.open(path, encoding='utf-8-sig').read()

# ---- 1. 时间线：采样号 -> (秒, 电力产出, 电力消耗, 场上对象数) ----
timeline = {}
for line in text.split('\n'):
    m = re.match(r'^\s+(\d+)\s+([\d.]+)s\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s', line)
    if m:
        idx = int(m.group(1))
        timeline[idx] = dict(sec=float(m.group(2)), money=int(m.group(3)),
                             power_out=int(m.group(4)), power_drain=int(m.group(5)),
                             objects=int(m.group(7)))

# ---- 2. 被跟踪对象的字段变化：采样号 -> [(对象号, 偏移, 旧, 新)] ----
changes = {}
cur_obj = None
obj_addr = {}
obj_new = {}
for line in text.split('\n'):
    m = re.match(r'^对象 #(\d+)\s+地址 0x([0-9A-F]+)\s+类型表 0x([0-9A-F]+)(.*)$', line)
    if m:
        cur_obj = int(m.group(1))
        obj_addr[cur_obj] = m.group(2)
        obj_new[cur_obj] = '新造出来的' in m.group(4)
        continue
    m = re.match(r'^\s+t=[\d.]+s\s+采样#(\d+)\s+\+0x([0-9A-F]+)\s+0x([0-9A-F]+) → 0x([0-9A-F]+)\s+\((\d+) → (\d+)\)', line)
    if m and cur_obj is not None:
        sample = int(m.group(1))
        changes.setdefault(sample, []).append(
            (cur_obj, int(m.group(2), 16), int(m.group(3), 16), int(m.group(4), 16),
             int(m.group(5)), int(m.group(6))))

# ---- 3. 输出 ----
print('=== 电力产出的跳变点 ===')
prev = None
jumps = []
for idx in sorted(timeline):
    t = timeline[idx]
    if prev is not None and t['power_out'] != prev['power_out']:
        delta = t['power_out'] - prev['power_out']
        jumps.append((idx, delta, prev['power_out'], t['power_out']))
        print('  采样#%-3d t=%5.1fs  %d → %d  (Δ%+d)　场上对象 %d → %d'
              % (idx, t['sec'], prev['power_out'], t['power_out'], delta, prev['objects'], t['objects']))
    prev = t

print()
print('=== 每次跳变时，被跟踪对象里"恰好变成这个数"的字段 ===')
for (idx, delta, before, after) in jumps:
    print()
    print('--- 采样#%d　Δ%+d（%d → %d）---' % (idx, delta, before, after))
    rows = changes.get(idx, [])
    hit_small = []      # 新值是个"像电力读数"的小整数
    hit_delta = []      # 新值恰好等于这次跳变的量
    for (obj, off, old, new, oldd, newd) in rows:
        if newd == abs(delta) and delta != 0:
            hit_delta.append((obj, off, oldd, newd))
        if 0 < newd <= 5000 and newd != oldd:
            hit_small.append((obj, off, oldd, newd))
    if hit_delta:
        print('  ★ 新值恰好 == |Δ| 的字段：')
        for (obj, off, oldd, newd) in hit_delta:
            print('     对象 #%d(0x%s)%s  +0x%X  %d → %d'
                  % (obj, obj_addr[obj], ' [新造]' if obj_new[obj] else '', off, oldd, newd))
    else:
        print('  （没有字段的新值恰好等于 Δ）')
    if hit_small:
        print('  新值在 1..5000 之间的字段（按新值排序，取前 25）：')
        for (obj, off, oldd, newd) in sorted(hit_small, key=lambda r: -r[3])[:25]:
            print('     对象 #%d(0x%s)%s  +0x%X  %d → %d'
                  % (obj, obj_addr[obj], ' [新造]' if obj_new[obj] else '', off, oldd, newd))
